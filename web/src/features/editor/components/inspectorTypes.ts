import type { TimelineItem } from '../../../api/types';

export type InspectorJson =
  | null | boolean | number | string
  | readonly InspectorJson[]
  | { readonly [key: string]: InspectorJson };

export type InspectorParams = Readonly<Record<string, InspectorJson>>;

export type InspectorEffect = Readonly<{
  effect: string;
  enabled?: boolean;
  params?: InspectorParams;
}>;

export type InspectorTransition = Readonly<{
  id?: string;
  type?: string;
  name?: string;
  params?: InspectorParams;
}> & (
  | { readonly id: string }
  | { readonly type: string }
  | { readonly name: string }
);

/** 宿主 UI 投影，保留既有秒 / dB / grade 语义；引擎单位转换由上层负责。 */
export type InspectorItemView = Readonly<Omit<TimelineItem, 'effects'>>;
export type InspectorItemPatch = Readonly<Partial<Omit<TimelineItem, 'id' | 'clipId' | 'effects'>>>;

export type InspectorClipView = Readonly<{
  key: string;
  /** null 表示尚无回读种子；0 是有效 revision。 */
  revision: number | null;
  item: InspectorItemView;
  effects: readonly InspectorEffect[];
  transition: InspectorTransition | null;
}>;

export type InspectorClipPatch = InspectorItemPatch & Readonly<{
  effects?: readonly InspectorEffect[];
  transition?: InspectorTransition | null;
}>;

export interface InspectorDrawerProps {
  /** false 时保留节点供收起动画使用。 */
  open?: boolean;
  /** undefined 使用旧 item 路径；null 明确表示空选，不回落到旧 item。 */
  clip?: InspectorClipView | null;
  /** 宿主事务入口；结果 / 失败通过新 props、revision 和 warnings 回传。 */
  onPatch?: (key: string, patch: InspectorClipPatch) => void;
  warnings?: readonly string[];
  /** 旧调用兼容入口；仅在未传 clip 时使用。 */
  item?: TimelineItem | null;
  revision?: number | null;
  onUpdateItem?: (patch: Partial<TimelineItem>, label: string) => void;
  label?: string | null;
  index?: number;
  total?: number;
  sequenceStart?: number;
  clipTitle?: string;
  sourceDuration?: number;
  fps?: number;
  onLocateSequence?: (time: number) => void;
  onClose?: () => void;
}
