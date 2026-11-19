import { getEditor, getVideoUrl, getPreviewAudioUrl } from '../../api/client';
import { importStudioFiles, importStudioPaths } from '../../api/studio';
import type { StudioImportResult } from '../../api/studio';
import type { FullEngineMediaSpec } from '../../engine/fullEngineSession';

export function mediaBinName(clip: {id:string; path:string; title?:string|null}) {
  return clip.path.split(/[\\/]/).pop() || clip.title || clip.id;
}

export function restoredBinNames(bin: {sourceId:string; name:string}[], clips: {id:string; path:string; title?:string|null}[]) {
  const names = new Map(clips.map(clip => [clip.id, mediaBinName(clip)]));
  return bin.map(item => item.name === item.sourceId && names.has(item.sourceId)
    ? {...item, name:names.get(item.sourceId)!} : item);
}

export const IMPORT_VIDEO_EXT = ['.mp4', '.mkv', '.mov', '.flv', '.webm', '.m4v', '.ts'];
export const IMPORT_ACCEPT = IMPORT_VIDEO_EXT.join(',');
const MAX_FILES = 200;
const MAX_TOTAL_BYTES = 16 * 1024 ** 3;

const extOf = (name: string) => { const dot = name.lastIndexOf('.'); return dot < 0 ? '' : name.slice(dot).toLowerCase(); };

/** 与后端 import-files 对齐（视频限定；字幕不走本入口）。返回第一条拒绝原因，通过为 null。 */
export function importRejectReason(files: readonly File[]): string | null {
  if (files.length === 0) return '没有选择文件。';
  const bad = files.filter(file => !IMPORT_VIDEO_EXT.includes(extOf(file.name)));
  if (bad.length) return `不支持的文件：${bad.slice(0, 3).map(file => file.name).join('、')}${bad.length > 3 ? ` 等 ${bad.length} 个` : ''}。支持 ${IMPORT_VIDEO_EXT.join(' ')}。`;
  if (files.length > MAX_FILES) return `一次最多导入 ${MAX_FILES} 个文件，当前 ${files.length} 个。`;
  if (files.reduce((sum, file) => sum + file.size, 0) > MAX_TOTAL_BYTES) return '一次导入总大小不能超过 16 GiB。';
  const seen = new Set<string>();
  for (const file of files) {
    const key = file.name.toLowerCase();
    if (seen.has(key)) return `同一次导入里有重名文件：${file.name}。请分开导入。`;
    seen.add(key);
  }
  return null;
}

export interface ImportSourceFailure { sourceId: string; message: string }
export interface ImportProbeResult {
  /** 全局素材库入库结果（后端只返回聚合计数；没有逐文件错误）。 */
  library: StudioImportResult;
  sources: Record<string, FullEngineMediaSpec>;
  failures: ImportSourceFailure[];
}
const errText = (error: unknown) => error instanceof Error ? error.message : String(error);

/** 对入库结果逐视频 clipId 探测元数据，构造可注册的媒体规格。不碰引擎。 */
async function probeImportedClips(library: StudioImportResult, skip: ReadonlySet<string>, isLive: () => boolean): Promise<ImportProbeResult | null> {
  const ids = [...new Set((library.clipIds ?? []).filter(id => typeof id === 'string' && id !== ''))];
  const sources: Record<string, FullEngineMediaSpec> = {};
  const failures: ImportSourceFailure[] = [];
  for (const id of ids) {
    if (!isLive()) return null;
    if (skip.has(id)) continue;
    try {
      const payload = await getEditor(id);
      if (![payload.duration, payload.width, payload.height].every(value => Number.isFinite(value) && value > 0)) throw new Error('素材时长或画面尺寸无效，可能仍在处理或文件已损坏');
      if (!payload.clip) throw new Error('导入结果不是媒体素材。');
      sources[id] = { url: getVideoUrl(id), audioUrl: getPreviewAudioUrl(id), name: mediaBinName(payload.clip), kind: 'video', durationSec: payload.duration,
        width: payload.width, height: payload.height, ...(typeof payload.hasAudio === 'boolean' ? { hasAudio: payload.hasAudio } : {}) };
    } catch (error) { failures.push({ sourceId: id, message: errText(error) }); }
  }
  return { library, sources, failures };
}

/** POST 真实文件 → 对每个视频 clipId 探测元数据，构造可注册的媒体规格。不碰引擎。 */
export async function importAndProbe(files: File[], skip: ReadonlySet<string>, isLive: () => boolean): Promise<ImportProbeResult | null> {
  return probeImportedClips(await importStudioFiles(files), skip, isLive);
}

/** 桌面选择器选中的本机路径 → /api/studio/import-paths → 同一探测链。复用真实路径导入，不伪造浏览器 File。 */
export async function importPathsAndProbe(paths: readonly string[], skip: ReadonlySet<string>, isLive: () => boolean): Promise<ImportProbeResult | null> {
  return probeImportedClips(await importStudioPaths([...paths]), skip, isLive);
}

/** 打开系统文件选择器；用户取消（无 change 事件，仅 cancel/无文件）返回 []，调用方零请求。必须在用户手势内调用。 */
export function pickVideoFiles(): Promise<File[]> {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file'; input.multiple = true; input.accept = IMPORT_ACCEPT; input.style.display = 'none';
    let settled = false;
    const done = (files: File[]) => { if (settled) return; settled = true; input.remove(); resolve(files); };
    input.addEventListener('change', () => done(Array.from(input.files ?? [])));
    input.addEventListener('cancel', () => done([]));
    document.body.appendChild(input);
    input.click();
  });
}

/** 快捷位置枚举：只允许这六个受控值跨桌面桥；宿主页收事件时会再校验一次。 */
export const IMPORT_MEDIA_LOCATIONS = ['computer', 'videos', 'music', 'pictures', 'downloads', 'documents'] as const;
export type ImportMediaLocation = typeof IMPORT_MEDIA_LOCATIONS[number];

const pathBasename = (p: string) => p.replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? p;

/** 与 importRejectReason 对齐的路径版（桌面选择器返回绝对路径，没有 File.size；总大小由后端导入与探测阶段把关）。 */
export function importRejectReasonPaths(paths: readonly string[]): string | null {
  if (paths.length === 0) return '没有选择文件。';
  const bad = paths.filter(p => !IMPORT_VIDEO_EXT.includes(extOf(pathBasename(p))));
  if (bad.length) return `不支持的文件：${bad.slice(0, 3).map(p => pathBasename(p)).join('、')}${bad.length > 3 ? ` 等 ${bad.length} 个` : ''}。支持 ${IMPORT_VIDEO_EXT.join(' ')}。`;
  if (paths.length > MAX_FILES) return `一次最多导入 ${MAX_FILES} 个文件，当前 ${paths.length} 个。`;
  const seen = new Set<string>();
  for (const p of paths) {
    const name = pathBasename(p);
    const key = name.toLowerCase();
    if (seen.has(key)) return `同一次导入里有重名文件：${name}。请分开导入。`;
    seen.add(key);
  }
  return null;
}
