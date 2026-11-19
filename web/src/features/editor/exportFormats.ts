import type { ExportFormat } from '../../api/types';

export interface ExportFormatSpec {
  value: ExportFormat;
  label: string;
  /** 成片容器扩展名。 */
  extension: '.mp4' | '.mov';
  /** 界面上一句话说明（编码器 + 用途）。 */
  summary: string;
}

/**
 * 导出格式的权威契约为 FilmCraftCliRunner.FilmCraftFormats；C# 契约测试校验此显示表。
 * 顺序即下拉框顺序：默认第一个是 H.264。DNxHR 使用 CLI 对应的 MOV 容器。
 */
export const EXPORT_FORMATS: readonly ExportFormatSpec[] = [
  { value: 'h264', label: 'H.264', extension: '.mp4', summary: 'H.264 · AAC · 通用分享' },
  { value: 'hevc', label: 'HEVC (H.265)', extension: '.mp4', summary: 'HEVC · AAC · 同码率更清晰' },
  { value: 'prores', label: 'ProRes 422', extension: '.mov', summary: 'ProRes 422 · 中间片剪辑' },
  { value: 'dnxhr', label: 'DNxHR', extension: '.mov', summary: 'DNxHR · Avid 工作流' },
];

const BY_VALUE = new Map<string, ExportFormatSpec>(EXPORT_FORMATS.map((spec) => [spec.value, spec]));

/** 归一化任意输入：未知值或空值都回落到 h264。 */
export function normalizeExportFormat(value: string | null | undefined): ExportFormat {
  const key = (value ?? '').trim().toLowerCase();
  return BY_VALUE.has(key) ? (key as ExportFormat) : 'h264';
}

export function exportFormatSpec(value: string | null | undefined): ExportFormatSpec {
  return BY_VALUE.get(normalizeExportFormat(value)) ?? EXPORT_FORMATS[0];
}

export function exportExtension(value: string | null | undefined): '.mp4' | '.mov' {
  return exportFormatSpec(value).extension;
}

/** 把输出路径换成当前编码应有的容器扩展名。 */
export function withExportExtension(path: string, value: string | null | undefined): string {
  return path.replace(/\.(mp4|mov|mxf)$/i, '') + exportExtension(value);
}

/** 旧四格式（FilmCraft CLI 契约，C# 测试按此表校验显示文案）。 */
export type LegacyExportFormat = 'h264' | 'hevc' | 'prores' | 'dnxhr';
/** R81 新增的后端 FFmpeg 原生格式 id（ExportFormat 去掉旧四格式）。 */
export type FfmpegExportFormatId = Exclude<ExportFormat, LegacyExportFormat>;

export interface FfmpegExportFormatSpec {
  id: FfmpegExportFormatId;
  label: string;
  /** 成片容器（图像序列为打包后的 .zip）。 */
  container: string;
  /** video=单文件视频，gif=动画 GIF，sequence=图像序列 zip，audio=纯音频。 */
  kind: 'video' | 'gif' | 'sequence' | 'audio';
  summary: string;
}

/**
 * R81：后端 FFmpeg 已经能真正产出的格式，与 src/LiveClipper.Core/EditorExportFormats.cs 同序同 id。
 * 这里刻意用 id/container 字段名（不是 value/extension），避免 FilmCraftCliRunnerTests 的显示表正则把它们算进
 * EXPORT_FORMATS；旧四格式的显示契约因此保持不变。
 * 不含 AV1 / APV / MXF / AAF / OMF：后端没有可靠编码路径。
 */
export const FFMPEG_EXPORT_FORMATS: readonly FfmpegExportFormatSpec[] = [
  {
    id: 'prores-proxy',
    label: 'Apple ProRes 422 Proxy',
    container: '.mov',
    kind: 'video',
    summary: 'ProRes 422 Proxy · 10-bit 4:2:2 · 粗剪代理',
  },
  {
    id: 'prores-lt',
    label: 'Apple ProRes 422 LT',
    container: '.mov',
    kind: 'video',
    summary: 'ProRes 422 LT · 10-bit 4:2:2 · 轻量中间片',
  },
  {
    id: 'prores-422',
    label: 'Apple ProRes 422',
    container: '.mov',
    kind: 'video',
    summary: 'ProRes 422 · 10-bit 4:2:2 · 标准中间片',
  },
  {
    id: 'dnxhr-lb',
    label: 'Avid DNxHR LB (8-bit)',
    container: '.mov',
    kind: 'video',
    summary: 'DNxHR LB · 8-bit 4:2:2 · 低码率 Avid 代理',
  },
  {
    id: 'dnxhr-sq',
    label: 'Avid DNxHR SQ (8-bit)',
    container: '.mov',
    kind: 'video',
    summary: 'DNxHR SQ · 8-bit 4:2:2 · Avid 广播工作流',
  },
  {
    id: 'dnxhr-hq',
    label: 'Avid DNxHR HQ (8-bit)',
    container: '.mov',
    kind: 'video',
    summary: 'DNxHR HQ · 8-bit 4:2:2 · 高码率 Avid 工作流',
  },
  {
    id: 'dnxhr-hqx',
    label: 'Avid DNxHR HQX (10-bit)',
    container: '.mov',
    kind: 'video',
    summary: 'DNxHR HQX · 10-bit 4:2:2 · HDR/Grade 中间片',
  },
  {
    id: 'mjpeg',
    label: 'QuickTime Motion JPEG',
    container: '.mov',
    kind: 'video',
    summary: 'Motion JPEG · 每帧 JPEG · QuickTime 兼容',
  },
  {
    id: 'webm',
    label: 'WebM (VP9 + Opus)',
    container: '.webm',
    kind: 'video',
    summary: 'VP9 + Opus · 开放网页格式',
  },
  {
    id: 'gif',
    label: '动画 GIF',
    container: '.gif',
    kind: 'gif',
    summary: '256 色动画 GIF · 无音轨 · 帧率上限 15',
  },
  {
    id: 'png',
    label: 'PNG 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 PNG · 打包 zip',
  },
  {
    id: 'tiff',
    label: 'TIFF 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 RGB TIFF · 打包 zip',
  },
  {
    id: 'bmp',
    label: 'BMP 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 24 位 BMP · 打包 zip',
  },
  {
    id: 'jpg',
    label: 'JPEG 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 JPEG (q=2) · 打包 zip',
  },
  {
    id: 'tga',
    label: 'Targa 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 24 位 Targa · 打包 zip',
  },
  {
    id: 'dpx',
    label: 'DPX 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 RGB DPX · 打包 zip',
  },
  {
    id: 'exr',
    label: 'OpenEXR 序列',
    container: '.zip',
    kind: 'sequence',
    summary: '逐帧 32 位浮点 EXR · 打包 zip',
  },
  {
    id: 'wav',
    label: 'Waveform Audio (16-bit)',
    container: '.wav',
    kind: 'audio',
    summary: 'PCM 16-bit WAV · 通用交付',
  },
  {
    id: 'wavlpcm',
    label: 'Waveform Audio (32-bit float)',
    container: '.wav',
    kind: 'audio',
    summary: 'IEEE 浮点 32-bit WAV · 混音/母带',
  },
  {
    id: 'wav51',
    label: 'Waveform Audio (5.1)',
    container: '.wav',
    kind: 'audio',
    summary: 'PCM 16-bit 5.1 (6 声道) WAV',
  },
  {
    id: 'wavbwf',
    label: '广播 WAV (BWF / bext)',
    container: '.wav',
    kind: 'audio',
    summary: 'PCM 24-bit WAV + bext 广播扩展',
  },
  {
    id: 'aiff',
    label: 'AIFF',
    container: '.aiff',
    kind: 'audio',
    summary: '大端 PCM 16-bit AIFF',
  },
  {
    id: 'aac',
    label: 'AAC 音频',
    container: '.m4a',
    kind: 'audio',
    summary: 'AAC-LC 192k · m4a 容器',
  },
  {
    id: 'flac',
    label: 'FLAC',
    container: '.flac',
    kind: 'audio',
    summary: '无损 FLAC · 归档交付',
  },
];

const FFMPEG_BY_ID = new Map<string, FfmpegExportFormatSpec>(
  FFMPEG_EXPORT_FORMATS.map((spec) => [spec.id, spec]),
);

/** R81 新增格式 id 列表（顺序与 C# EditorExportFormats.Ids 一致）。 */
export const FFMPEG_EXPORT_FORMAT_IDS: readonly FfmpegExportFormatId[] =
  FFMPEG_EXPORT_FORMATS.map((spec) => spec.id);

export function isFfmpegExportFormat(value: string | null | undefined): value is FfmpegExportFormatId {
  return FFMPEG_BY_ID.has((value ?? '').trim().toLowerCase());
}

/** 后端新增格式的描述；未知（含旧四格式）返回 undefined。 */
export function ffmpegExportFormatSpec(
  value: string | null | undefined,
): FfmpegExportFormatSpec | undefined {
  return FFMPEG_BY_ID.get((value ?? '').trim().toLowerCase());
}

/** 四格式 + 新增格式的容器扩展名；未知值回落到旧四格式的 h264 规则。 */
export function exportContainerExtension(value: string | null | undefined): string {
  return ffmpegExportFormatSpec(value)?.container ?? exportExtension(value);
}
