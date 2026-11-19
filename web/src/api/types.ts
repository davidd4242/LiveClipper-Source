export interface ClipDto {
  id: string;
  eventId: string;
  path: string;
  srtPath: string | null;
  createdAt: string;
  title?: string | null;
}

export type TimelineItemKind = 'video' | 'hook' | 'insert' | 'graphic';
export type TimelineFilter = 'none' | 'warm' | 'cool' | 'mono' | 'vivid';
/** 成片编码矩阵：与 filmcraft-cli 的 --format 取值一致。 */
export type ExportFormat = 'h264' | 'hevc' | 'prores' | 'dnxhr'
  // R81：后端 FFmpeg 原生格式（与 src/LiveClipper.Core/EditorExportFormats.cs 一一对应）
  | 'prores-proxy' | 'prores-lt' | 'prores-422'
  | 'dnxhr-lb' | 'dnxhr-sq' | 'dnxhr-hq' | 'dnxhr-hqx'
  | 'mjpeg' | 'webm' | 'gif'
  | 'png' | 'tiff' | 'bmp' | 'jpg' | 'tga' | 'dpx' | 'exr'
  | 'wav' | 'wavlpcm' | 'wav51' | 'wavbwf' | 'aiff' | 'aac' | 'flac';
export type TimelineMotion = 'none' | 'push' | 'shake';

/** Host-provenance of a timeline item generated from a source event. */
export interface TimelineEventPlacement { version: 1; sourceId: string; eventId: string }

/** 受管理事件片段的显式清空标记（v1）：该源的全部事件已取消勾选到零，允许 timeline=[]；空序列不可成片/导出。 */
export interface TimelineEventSelectionClear { version: 1; sourceId: string }

export interface TimelineItem {
  id: string;
  clipId: string;
  eventPlacement?: TimelineEventPlacement | null;
  start: number;
  end: number;
  kind: TimelineItemKind;
  zoom: number;
  filter: TimelineFilter;
  motion: TimelineMotion;
  stickerAssetId: string | null;
  stickerX: number;
  stickerY: number;
  stickerWidth: number;
  soundAssetId: string | null;
  stickerRotation: number;
  gapBefore?: number;
  /** Absolute sequence position; all items on this track use explicit starts. */
  sequenceStart?: number | null;
  speed?: number;
  track?: number;
  rotation?: number;
  opacity?: number;
  positionX?: number;
  positionY?: number;
  disabled?: boolean;
  volumeDb?: number;
  linkGroup?: string | null;
  audioDetached?: boolean;
  titleText?: string | null;
  titleFont?: 'Microsoft YaHei' | 'Segoe UI' | 'Arial' | null;
  titleSize?: number;
  titleColor?: string;
  titleX?: number;
  titleY?: number;
  graphicKind?: 'title' | 'box' | 'lowerThirdBlock' | 'line' | 'callout-pointer' | 'end-credits';
  graphicBoxColor?: string;
  graphicBoxOpacity?: number;
  graphicBoxX?: number;
  graphicBoxY?: number;
  graphicBoxW?: number;
  graphicBoxH?: number;
  graphicLineThickness?: number;
  previewOnly?: boolean;
  fadeIn?: number;
  fadeOut?: number;
  /**
   * 音量包络关键帧：time 为片段输出时间轴内的相对秒（0 .. itemOutputDuration），
   * db 范围 [-60, 12]；null/缺省表示无包络。点间线性插值，首末点之外保持端点值。
   */
  volumeKeyframes?: VolumeKeyframe[] | null;
  /**
   * 音频效果链（按数组顺序串联，enabled=false 旁路）。null/缺省表示无效果，
   * 旧工程保持逐字节行为不变；参数一律业务单位（dB / ms / :1），后端负责 clamp。
   */
  effects?: AudioEffectInstance[] | null;
  /**
   * 变换参数关键帧（冻结模型，见 docs/keyframe-model-freeze.md）：键为参数名，
   * 值为按时间升序的关键帧数组；时间单位是片段输出时间轴内的相对秒。
   * null/缺省表示全部参数使用静态值，旧工程 JSON 不变。
   */
  keyframes?: Partial<Record<ParamKeyframeProperty, ParamKeyframe[]>> | null;
  /** 调色参数（P1.6-b）。null/缺省表示不动画面。 */
  grade?: ColorGrade | null;
  /** 静态蒙版 v1（渲染端只消费第一条启用的蒙版）。null/缺省表示无蒙版。 */
  masks?: MaskInstance[] | null;
  /**
   * 序列嵌套引用 v1：只想 EditorProject.nestedSequences 里的子序列 id。
   * 一级嵌套、不可裁剪；null/缺省表示普通片段。
   */
  nestedSequenceId?: string | null;
}

/**
 * 片段调色参数（P1.6-b）：亮度 -1..1、对比度/饱和度 0..3（1 为原值）、色温 -1..1。
 * null/缺省表示不动画面，旧工程 JSON 不变。
 */
export interface ColorGrade {
  brightness?: number;
  contrast?: number;
  saturation?: number;
  temperature?: number;
}

/** 可关键帧的变换参数名（冻结白名单）。 */
export type ParamKeyframeProperty = 'scale' | 'rotation' | 'positionX' | 'positionY' | 'opacity';

/** 通用参数关键帧：time 为片段输出时间轴相对秒，value 为该参数的业务值。 */
export interface ParamKeyframe {
  time: number;
  value: number;
}

/** 静态蒙版 v1。坐标相对画面：x/y 为 -0.5..0.5 偏移，w/h 为 0..2 比例。 */
export interface MaskInstance {
  id: string;
  type: 'rect' | 'ellipse' | 'polygon';
  enabled: boolean;
  x: number;
  y: number;
  w: number;
  h: number;
  rotation: number;
  feather: number;
  opacity: number;
  /**
   * 多边形蒙版顶点（仅 type='polygon' 使用）：0..1 归一化画面坐标，至少 3 个点。
   * rect/ellipse 忽略该字段；渲染端按点集画出多边形 matte。
   */
  points?: Array<{ x: number; y: number }> | null;
}

/**
 * 嵌套序列 v1（后端契约：src/LiveClipper.Core/EditorRenderService.cs:50-54）。
 * 父时间线片段用 TimelineItem.nestedSequenceId 引用；渲染端 flatten 时把子序列 items 展开到主时间线。
 * v1 只读预览，子序列自身不再展开（一级嵌套）。
 */
export interface NestedSequence {
  id: string;
  name: string;
  timeline: TimelineItem[];
  transitions?: EditorTransition[] | null;
  fps: number;
  resolution: number;
}

/** 音频效果类型白名单（与后端 AudioEffectType 对齐）。parametric_eq 的参数由专用控件写入 35 个键。 */
export type AudioEffectType = 'compressor' | 'noiseGate' | 'parametric_eq';

/** 音频效果实例，params 为业务单位参数（键名见 docs/filmcraft-parity-next4.md §7）。 */
export interface AudioEffectInstance {
  id: string;
  type: AudioEffectType;
  enabled: boolean;
  params: Record<string, number>;
}

/** 音量包络关键帧（时间线音频橡皮筋）。 */
export interface VolumeKeyframe {
  /** 片段输出起点起的相对秒。 */
  time: number;
  /** 音量增益，dB。 */
  db: number;
}

export type EditorTransitionKind = 'cross-dissolve' | 'dip-to-black' | 'dip-to-white'
  | 'wipe-left' | 'wipe-right' | 'wipe-up' | 'wipe-down'
  | 'catalog-wipe-up' | 'catalog-wipe-right' | 'catalog-wipe-down' | 'catalog-wipe-left' | 'linear-wipe-up' | 'linear-wipe-right' | 'linear-wipe-down' | 'linear-wipe-left'
  | 'additive-dissolve' | 'non-additive-dissolve' | 'iris-box' | 'iris-cross' | 'iris-diamond' | 'iris-round'
  | 'barn-doors-vertical' | 'barn-doors-horizontal'
  | 'soft-wipe-up' | 'soft-wipe-right' | 'soft-wipe-down' | 'soft-wipe-left'
  | 'gradient-wipe-up' | 'gradient-wipe-right' | 'gradient-wipe-down' | 'gradient-wipe-left'
  | 'panel-wipe' | 'vr-iris-wipe' | 'vr-gradient-wipe' | 'flash' | 'ray' | 'vr-light-rays' | 'solarize'
  | 'flare' | 'light-sweep' | 'plateau-wipe' | 'shape-flow';
export type EditorTransitionAlign = 'center' | 'start' | 'end';

/**
 * 剪辑点转场：挂在剪点右侧片段（itemId）的入点上，横跨两个相邻片段。
 * 规格见 docs/transitions-spec.md：duration 保存请求时长，实际生效值由片段长度 clamp。
 */
export interface EditorTransition {
  id: string;
  itemId: string;
  track: number;
  kind: EditorTransitionKind;
  duration: number;
  align: EditorTransitionAlign;
  layout?: 'overlap' | 'preserve';
}

export interface TranscriptSegment {
  start: number;
  end: number;
  text: string;
}

export interface TranscriptPreview {
  language: string;
  segments: TranscriptSegment[];
  text: string;
}

/** 独立字幕 cue（序列秒），镜像 C# LiveClipper.Core.TimelineCaption；样式缺省回落到渲染默认（size 34 / margin 36）。 */
export interface TimelineCaption {
  id: string;
  start: number;
  end: number;
  text: string;
  font?: string | null;
  size?: number | null;
  /** #RRGGBB */
  color?: string | null;
  /** 字幕中心点，相对画面 0–1；X/Y 必须成对。 */
  positionX?: number | null;
  positionY?: number | null;
  outline?: number | null;
  margin?: number | null;
  sourceClipId?: string | null;
  sequenceItemId?: string | null;
  sourceCueIndex?: number | null;
  /** 源媒体秒，原样保留。 */
  sourceStart?: number | null;
  sourceEnd?: number | null;
  manual?: boolean;
}

export interface EditorProject {
  canvasWidth?: number | null;
  canvasHeight?: number | null;
  start: number;
  end: number;
  timeline: TimelineItem[] | null;
  transitions?: EditorTransition[] | null;
  nestedSequences?: Record<string, NestedSequence> | null;
  burnSubtitles: boolean;
  selectedSegments: number[] | null;
  voiceOnly: boolean;
  musicAssetId: string | null;
  effectAssetId: string | null;
  voiceVolume: number;
  musicVolume: number;
  effectVolume: number;
  masterVolume: number;
  effectAt: number;
  subtitleFont: string;
  subtitleSize: number;
  subtitleColor: string;
  subtitlePosition: 'top' | 'middle' | 'bottom';
  resolution: number;
  fps: number;
  /** 成片编码（P1.6-b 导出格式矩阵）；null/缺省 = h264。 */
  format?: ExportFormat | null;
  bitrateKbps: number;
  sentenceMode: boolean;
  duckMusic: boolean;
  subtitleOutline: number;
  subtitleMargin: number;
  /** 后端 Snapshot 冻结的派生字幕快照（只读，勿当权威编辑数据）。 */
  sourceTranscripts: Record<string, TranscriptSegment[]> | null;
  /**
   * 源媒体秒（source seconds）为键的用户字幕覆盖，权威编辑数据。
   * null/缺省 → 沿用 store 字幕；`key: []` → 显式清空该源；非空 → 替代该源全部字幕。
   */
  captionOverrides?: Record<string, TranscriptSegment[]> | null;
  /**
   * 独立字幕轨（序列秒），对应 C# TimelineCaption。null/缺省 = 沿用旧的源字幕动态映射；
   * 非 null（含 []）= 该数组是字幕唯一权威，[] 表示字幕轨显式为空，不再补回源字幕。
   */
  timelineCaptions?: TimelineCaption[] | null;
  silenceTrimBackup?: TimelineItem[] | null;
  silenceTrimmed?: boolean;
  silenceTrimmedSeconds?: number;
  mutedTracks?: number[] | null;
  soloedTracks?: number[] | null;
  hiddenTracks?: number[] | null;
  lockedTracks?: number[] | null;
  /** V1 内嵌音频静音（独立于 mutedTracks 的 4..6 音频轨范围）；缺省 = 不静音。 */
  baseAudioMuted?: boolean;
  markers?: number[] | null;
  trackOpacities?: number[] | null;
  /** 受管理事件清空标记（v1，见 TimelineEventSelectionClear）：null/缺省=无清空语义（空 timeline 照旧拒绝）；非空=显式空序列合法但不可成片/导出。 */
  clearedEventSelections?: TimelineEventSelectionClear[] | null;
  /** Retained seconds only for explicit managed-empty timeline; annotations keep their original times. */
  emptySequenceDuration?: number | null;
}

export interface StudioEvent {
  id: string;
  clipId: string;
  start: number;
  end: number;
  title: string;
  summary: string;
  hook: string;
  source: string;
}

export interface AutoPlanReport {
  clipId: string;
  status: string;
  source: string;
  title: string;
  summary: string;
  start: number;
  end: number;
  keepCount: number;
  segmentCount: number;
  eventCount: number;
  trimmedSeconds: number;
  updatedAt: string;
  error?: string | null;
}

/** /api/ai/analyze 的 advice：整场总结、可用文案与切片建议。 */
export interface AiAdvice {
  summary: string;
  captions: string[];
  tips: string[];
}

export interface AiAnalyzeResponse {
  events: StudioEvent[];
  advice: AiAdvice | null;
  adviceError: string | null;
}

export interface EditorProjectIdentity {
  id: string;
  name: string;
  width: number;
  height: number;
  fps: number;
  createdAt: string;
}

export interface EditorPayload {
  clip: ClipDto | null;
  projectIdentity?: EditorProjectIdentity;
  duration: number;
  width: number;
  height: number;
  /** 源素材是否带音轨（后端 ffprobe 结果；缺省视为未知，按带音轨处理）。 */
  hasAudio?: boolean;
  transcript: TranscriptPreview | null;
  project: EditorProject;
  stems: unknown;
  events: StudioEvent[];
  advice: AiAdvice | null;
  /** 工程 revision（GET 时的服务器版本），下一次 PUT 作为 X-Base-Revision。旧后端不返回。 */
  revision?: number;
  /** 服务器存储纪元（预留字段：StudioEndpoints/StudioStore 目前不返回，前端也未读取）。 */
  epoch?: string;
}

export type LiveAvailability = 'Unknown' | 'Offline' | 'Live';
export type LiveSessionState =
  | 'Starting'
  | 'Recording'
  | 'Reconnecting'
  | 'Finalizing'
  | 'Ended'
  | 'Interrupted'
  | 'Failed';

export interface LiveRoomStatus {
  roomId: number;
  availability: LiveAvailability;
  running: boolean;
  capturing: boolean;
  room: {
    roomId: number;
    availability: LiveAvailability;
    title: string;
    streamer: string;
    checkedAt: string;
  } | null;
  session: {
    id: string;
    roomId: number;
    state: LiveSessionState;
    startedAt: string;
    endedAt: string | null;
    resumedFrom: string | null;
    restartCount: number;
    lastSequence: number;
    lastError: string | null;
    isOpen: boolean;
  } | null;
  lastError: string | null;
  segmentCount: number;
  danmakuCount: number;
  restartCount: number;
  recoveredSessions: number;
  updatedAt: string;
}

export interface LiveResolution {
  input: number;
  roomId: number;
  resolvedFrom: 'room' | 'uid';
  title: string;
  streamer: string;
  availability: LiveAvailability;
}

export interface LiveRoomRegistration {
  roomId: number;
  active: boolean;
  addedAt: string;
  updatedAt: string;
}

export interface LiveRoomsResponse {
  rooms: LiveRoomStatus[];
  registrations?: LiveRoomRegistration[];
  maxRooms: number;
}

export interface LiveStorageStatus {
  checkedAt: string;
  enforced: boolean;
  totalBytes: number;
  metadataBytes: number;
  freeBytes: number;
  maxTotalBytes: number;
  minFreeBytes: number;
  sessionDirectories: number;
  protectedSessions: number;
  expiredSessions: number;
  deletedSessions: number;
  deletedBytes: number;
  quotaExceeded: boolean;
  warnings: string[];
}

export interface HealthResponse {
  ok: boolean;
  app: string;
  dataRoot: string;
}

export type RenderStatus =
  | 'pending'
  | 'running'
  | 'paused'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'unknown';

export interface RenderDto {
  id: string;
  clipId: string;
  projectId?: string | null;
  /** Null/absent only for renders created before server origin recording. */
  revision?: number | null;
  epoch?: string | null;
  outputPath: string;
  createdAt: string;
  status: RenderStatus;
  error: string | null;
}

export interface EditorSequenceSummary {
  id: string;
  name: string;
  createdAt: string;
}

export interface EditorSequencePayload {
  id: string;
  name: string;
  project: EditorProject;
}

export interface ProjectRevisionInfo {
  fileName: string;
  createdAt: string;
  size: number;
}

export interface JobDto {
  id: string;
  type: string;
  eventId: string;
  projectId?: string | null;
  /** edit-render origin, keyed by render ID rather than the shared source event. */
  clipId?: string | null;
  revision?: number | null;
  epoch?: string | null;
  status: string;
  progress: number;
  error: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  message?: string | null;
}
