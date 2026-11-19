/**
 * 时间线工具栏的纯布局决策（无 DOM 依赖，便于单测）。
 *
 * 规则：
 * 1. 标签模式（图标 + 文字）的左组放得下 → 全部内联；
 * 2. 否则切到图标模式，每个动作占固定 slotWidth；放不下的"尾部动作"全部进入「更多」菜单，
 *    「更多」按钮自己也占一个槽位。永远不裁切半个按钮，也不隐藏动作。
 */
export interface ToolbarLayoutInput {
  /** 工具栏内容区宽度（已扣除左右 padding）。 */
  contentWidth: number;
  /** 标签模式下左组/右组的自然宽度（首次以标签模式渲染时测得）。 */
  labelsMainWidth: number;
  labelsRightWidth: number;
  /** 图标模式下右组宽度；尚未测得时传 null（先按 labelsRightWidth 保守估计）。 */
  iconsRightWidth: number | null;
  /** 工具栏顶层子元素之间的 gap（左组 / 间隔 / 右组之间共 2 处）。 */
  gap: number;
  /** 左组内按钮间 gap。 */
  mainGap: number;
  /** 间隔元素最小宽度。 */
  minSpacer: number;
  /** 图标模式下单个按钮宽度。 */
  slotWidth: number;
  /** 动作总数。 */
  total: number;
}

export interface ToolbarLayout {
  mode: 'labels' | 'icons';
  /** 内联显示的动作数量；其余进入「更多」。 */
  visible: number;
}

export function planToolbarLayout(input: ToolbarLayoutInput): ToolbarLayout {
  const { contentWidth, gap, mainGap, minSpacer, slotWidth, total } = input;
  const frame = 2 * gap + minSpacer;
  if (input.labelsMainWidth + input.labelsRightWidth + frame <= contentWidth) {
    return { mode: 'labels', visible: total };
  }
  const right = input.iconsRightWidth ?? input.labelsRightWidth;
  const avail = contentWidth - right - frame;
  const slots = Math.floor((avail + mainGap) / (slotWidth + mainGap));
  if (slots >= total) return { mode: 'icons', visible: total };
  return { mode: 'icons', visible: Math.max(0, slots - 1) };
}
