import type { TimelineItem } from '../../api/types';

/**
 * P1.6-b 基本图形模板：只描述标题图层的排版参数，不锁定文字内容。
 * 尺寸用 720p 基准（后端按画面高度等比换算），坐标是画面归一化位置。
 */
export interface GraphicTemplate {
  id: string;
  label: string;
  description: string;
  titleSize: number;
  titleColor: string;
  titleX: number;
  titleY: number;
  /** 空标题时填入的占位文字。 */
  sample: string;
}

export const GRAPHIC_TEMPLATES: readonly GraphicTemplate[] = [
  {
    id: 'center-title',
    label: '居中主标题',
    description: '画面正中的大标题，适合开场与章节页。',
    titleSize: 96,
    titleColor: '#FFFFFF',
    titleX: 0.5,
    titleY: 0.5,
    sample: '主标题文字',
  },
  {
    id: 'lower-third',
    label: '下三分之一',
    description: '人物介绍/信息条位置，避开画面主体。',
    titleSize: 48,
    titleColor: '#FFFFFF',
    titleX: 0.5,
    titleY: 0.82,
    sample: '姓名 · 身份',
  },
  {
    id: 'corner-tag',
    label: '右上角标',
    description: '小字号角标，适合期数、栏目、品牌标识。',
    titleSize: 36,
    titleColor: '#FFD166',
    titleX: 0.78,
    titleY: 0.12,
    sample: 'EP.01',
  },
  {
    id: 'quote-line',
    label: '引用条',
    description: '偏下的引用文字，留出上方画面空间。',
    titleSize: 42,
    titleColor: '#E8F1FF',
    titleX: 0.5,
    titleY: 0.74,
    sample: '「引用文字」',
  },
  {
    id: 'big-number',
    label: '大号数字',
    description: '极简数字卡片，用于排行、比分、章节编号。',
    titleSize: 140,
    titleColor: '#FFFFFF',
    titleX: 0.5,
    titleY: 0.45,
    sample: '01',
  },
];

const BY_ID = new Map<string, GraphicTemplate>(GRAPHIC_TEMPLATES.map((template) => [template.id, template]));

export function graphicTemplate(id: string | null | undefined): GraphicTemplate | null {
  const key = (id ?? '').trim();
  return key ? (BY_ID.get(key) ?? null) : null;
}

/**
 * 套用模板：排版参数一律覆盖；标题文字只在为空时填占位，
 * 避免把用户写好的文案替换掉。
 */
export function templatePatch(item: Pick<TimelineItem, 'titleText'>, template: GraphicTemplate): Partial<TimelineItem> {
  const text = item.titleText?.trim() ? item.titleText : template.sample;
  return {
    titleText: text,
    titleSize: template.titleSize,
    titleColor: template.titleColor,
    titleX: template.titleX,
    titleY: template.titleY,
  };
}

/** 当前片段是否已经是该模板的排版（文字内容不参与判定）。 */
export function templateMatches(item: TimelineItem, template: GraphicTemplate): boolean {
  // 没有标题文字时不存在"当前模板"：TimelineItem 的默认排版（48/#FFFFFF/.5/.82）恰好等于下三分之一，
  // 不挡这一下会让空白片段误报"已套用模板"。
  if (!item.titleText?.trim()) return false;
  return (
    Math.abs((item.titleSize ?? 0) - template.titleSize) < 0.001 &&
    (item.titleColor ?? '').toUpperCase() === template.titleColor.toUpperCase() &&
    Math.abs((item.titleX ?? 0) - template.titleX) < 0.001 &&
    Math.abs((item.titleY ?? 0) - template.titleY) < 0.001
  );
}

/** 标题只在 V3 轨道渲染（后端 TrackOf(item) === 3 才画 drawtext）。 */
export const TITLE_TRACK = 3;

export function needsTitleTrack(item: Pick<TimelineItem, 'track'>): boolean {
  return (item.track ?? 1) !== TITLE_TRACK;
}
