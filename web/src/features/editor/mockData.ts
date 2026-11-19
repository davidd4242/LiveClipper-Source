import type {
  Chapter,
  HighlightCandidate,
  MediaItem,
  ProjectNode,
  TimelineTrack,
  TranscriptCue,
} from './model';

export const projectName = '直播回放_09-28';

export const projectNodes: ProjectNode[] = [
  { id: 'main', label: '01_主素材', kind: 'folder', depth: 0, expanded: true },
  {
    id: 'vod',
    label: '直播回放_09-28',
    kind: 'video',
    depth: 1,
    expanded: true,
    duration: '05:42:18',
    selected: true,
  },
  {
    id: 'highlights',
    label: '高能片段_精选',
    kind: 'video',
    depth: 2,
    duration: '00:29:47',
  },
  {
    id: 'subtitles',
    label: '转写字幕_中文',
    kind: 'subtitle',
    depth: 2,
    duration: 'SRT',
  },
  { id: 'sound', label: '02_声音', kind: 'folder', depth: 0, expanded: true },
  {
    id: 'ambience',
    label: '氛围铺底_低音',
    kind: 'audio',
    depth: 1,
    duration: '02:14',
  },
  {
    id: 'transition',
    label: '转场音效_01',
    kind: 'audio',
    depth: 1,
    duration: '00:03',
  },
  { id: 'graphics', label: '03_图形与标题', kind: 'folder', depth: 0 },
  { id: 'proxy', label: '04_代理文件', kind: 'folder', depth: 0 },
  { id: 'exports', label: '05_导出队列', kind: 'folder', depth: 0 },
];

export const mediaItems: MediaItem[] = [
  {
    id: 'media-main',
    label: '直播回放_09-28',
    kind: 'video',
    duration: '05:42:18',
    colors: ['#614d3f', '#2b3947'],
    selected: true,
  },
  {
    id: 'media-highlights',
    label: '高能片段_精选',
    kind: 'video',
    duration: '00:29:47',
    colors: ['#4f405c', '#263b54'],
  },
  {
    id: 'media-cover',
    label: '开场标题_暖色',
    kind: 'title',
    duration: '00:12:08',
    colors: ['#6c5037', '#345347'],
  },
  {
    id: 'media-ambience',
    label: '氛围铺底_低音',
    kind: 'audio',
    duration: '02:14',
    colors: ['#283f34', '#283e30'],
  },
  {
    id: 'media-transition',
    label: '转场音效_01',
    kind: 'audio',
    duration: '00:03',
    colors: ['#2f4858', '#293b42'],
  },
  {
    id: 'media-subtitles',
    label: '字幕_中文',
    kind: 'title',
    duration: 'SRT',
    colors: ['#403f5b', '#273248'],
  },
];

export const candidates: HighlightCandidate[] = [
  {
    id: 'candidate-1',
    start: '24:36',
    end: '25:16',
    startSeconds: 1476,
    endSeconds: 1516,
    title: '他真的冲了！情绪爆点',
    score: 94,
    rationale: '3 秒内音量两次超过阈值，弹幕密度同步升高，适合做短视频开场。',
    quote: '“等一下，这里是不是有点不对劲？”',
  },
  {
    id: 'candidate-2',
    start: '18:57',
    end: '19:42',
    startSeconds: 1137,
    endSeconds: 1182,
    title: '反客为主，弹幕齐刷问号',
    score: 88,
    rationale: '短时间高频关键词重复，观众反应集中，适合做中段转折。',
    quote: '“不是，这也能算我赢？”',
  },
  {
    id: 'candidate-3',
    start: '31:12',
    end: '31:58',
    startSeconds: 1872,
    endSeconds: 1918,
    title: '沉默三秒后的突然破防',
    score: 84,
    rationale: '停顿后出现明显语气变化，前后反差大，建议补一个轻音效。',
    quote: '“我刚才是不是听错了？”',
  },
  {
    id: 'candidate-4',
    start: '35:08',
    end: '35:44',
    startSeconds: 2108,
    endSeconds: 2144,
    title: '临场改口，弹幕笑成一片',
    score: 79,
    rationale: '说话人自己纠正后停顿，弹幕出现密集“哈哈哈”，适合做轻喜剧段落。',
    quote: '“不是，我刚才是不是说反了？”',
  },
  {
    id: 'candidate-5',
    start: '41:20',
    end: '42:02',
    startSeconds: 2480,
    endSeconds: 2522,
    title: '一句反问把节奏拉回来',
    score: 74,
    rationale: '连续短句后出现明确反问，观众注意力回升，适合做章节转场。',
    quote: '“你们不会真觉得这波是我算好的吧？”',
  },
  {
    id: 'candidate-6',
    start: '46:55',
    end: '47:26',
    startSeconds: 2815,
    endSeconds: 2846,
    title: '收尾前的意外彩蛋',
    score: 68,
    rationale: '音量不高但语义反差明显，作为片尾彩蛋比正片更合适。',
    quote: '“等一下，刚才那个是不是没关？”',
  },
];

export const transcriptCues: TranscriptCue[] = [
  { id: 'cue-1', at: '24:31', speaker: '主播', text: '刚才那个位置他是怎么绕过去的？我完全没看懂。' },
  {
    id: 'cue-2',
    at: '24:36',
    speaker: '主播',
    text: '等一下等一下，这里是不是有点不对劲。',
    emphasis: '这里是不是有点不对劲',
  },
  { id: 'cue-3', at: '24:42', speaker: '嘉宾', text: '你先别动，我看一下地图。' },
  {
    id: 'cue-4',
    at: '24:48',
    speaker: '主播',
    text: '他冲了！他真的冲了！这一下太突然了。',
    emphasis: '他冲了！他真的冲了！',
  },
  { id: 'cue-5', at: '24:53', speaker: '主播', text: '这波如果打成了，直接翻盘。' },
  { id: 'cue-6', at: '24:57', speaker: '嘉宾', text: '啊——这也行？这也太夸张了吧。' },
  { id: 'cue-7', at: '25:02', speaker: '主播', text: '哈哈哈，我人直接站起来了。' },
  { id: 'cue-8', at: '25:07', speaker: '主播', text: '快截弹幕，满屏都是问号。' },
  { id: 'cue-9', at: '25:12', speaker: '嘉宾', text: '这真的是哪个版本啊？' },
  { id: 'cue-10', at: '25:17', speaker: '嘉宾', text: '不是不是，真不是，他自己都懵了。' },
];

export const chapters: Chapter[] = [
  { id: 'chapter-1', at: '00:00', title: '开场与今天的目标', heat: 32, note: '建立预期' },
  { id: 'chapter-2', at: '06:18', title: '第一轮挑战', heat: 48, note: '节奏上升' },
  { id: 'chapter-3', at: '12:44', title: '意外翻车', heat: 61, note: '弹幕开始活跃' },
  { id: 'chapter-4', at: '18:57', title: '反客为主', heat: 88, note: '候选 02' },
  { id: 'chapter-5', at: '24:36', title: '他真的冲了', heat: 94, note: '本场峰值' },
  { id: 'chapter-6', at: '31:12', title: '沉默后破防', heat: 84, note: '候选 03' },
];

export const timelineTracks: TimelineTrack[] = [
  {
    id: 'V3',
    name: '图形与标题',
    kind: 'video',
    clips: [
      { id: 'v3-title', label: '片头标题_暖色', left: 3, width: 10, variant: 'title' },
      { id: 'v3-badge', label: '高能提示', left: 68, width: 8, variant: 'title' },
    ],
  },
  {
    id: 'V2',
    name: '叠加层',
    kind: 'video',
    clips: [
      { id: 'v2-corner', label: '角标 / 边框', left: 10, width: 16, variant: 'video-b' },
      { id: 'v2-sub', label: '字幕底板', left: 27, width: 15, variant: 'video-b' },
    ],
  },
  {
    id: 'V1',
    name: '主画面',
    kind: 'video',
    clips: [
      { id: 'v1-open', label: '01 开场铺垫', left: 5, width: 10, variant: 'video-a' },
      { id: 'v1-climax', label: '02 高能连续', left: 16, width: 15, variant: 'video-b', selected: true },
      { id: 'v1-turn', label: '03 反转瞬间', left: 31, width: 14, variant: 'video-c' },
      { id: 'v1-reaction', label: '04 观众反应', left: 45, width: 18, variant: 'video-a' },
      { id: 'v1-close', label: '05 收束', left: 64, width: 11, variant: 'video-b' },
    ],
  },
  {
    id: 'A1',
    name: '人声',
    kind: 'audio',
    clips: [
      { id: 'a1-dialogue', label: 'DIALOGUE · -10.4 LUFS', left: 4, width: 72, variant: 'audio' },
    ],
  },
  {
    id: 'A2',
    name: '音乐',
    kind: 'audio',
    clips: [
      { id: 'a2-music', label: 'MUSIC BED · -24.0 LUFS', left: 4, width: 52, variant: 'music' },
      { id: 'a2-outro', label: 'OUTRO', left: 59, width: 14, variant: 'music' },
    ],
  },
  {
    id: 'A3',
    name: '音效',
    kind: 'audio',
    clips: [
      { id: 'a3-woosh', label: 'WHOOSH', left: 31, width: 7, variant: 'audio' },
      { id: 'a3-impact', label: 'IMPACT', left: 47, width: 8, variant: 'audio' },
    ],
  },
];

export const timelineRuler = ['00:24:00', '00:24:15', '00:24:30', '00:24:45', '00:25:00', '00:25:15', '00:25:30', '00:25:45', '00:26:00', '00:26:15', '00:26:30'];
