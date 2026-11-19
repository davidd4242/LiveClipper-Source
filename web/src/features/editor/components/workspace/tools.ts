import {
  ChevronsLeftRight,
  Flag,
  Gauge,
  GitCommitHorizontal,
  MousePointer2,
  MousePointerClick,
  MoveHorizontal,
  Scissors,
  Waves,
  type LucideIcon,
} from 'lucide-react';
import type { EditorTool } from '../ToolRail';

/**
 * 「工具」窗口与时间线左侧工具轨使用同一套九个工具。
 * 注意：ToolRail.tsx 不在本任务写范围内，这里保留同序元数据；
 * 两处顺序、快捷键、图标需保持一致（见交付说明中的重复来源注明）。
 */
export type EditorToolOption = {
  id: EditorTool;
  label: string;
  hint: string;
  shortcut: string;
  icon: LucideIcon;
};

export const EDITOR_TOOL_OPTIONS: EditorToolOption[] = [
  {
    id: 'select',
    label: '选择工具',
    hint: '点选片段，拖动排序，拖左右边缘裁剪',
    shortcut: 'V',
    icon: MousePointer2,
  },
  {
    id: 'track',
    label: '轨道选择工具',
    hint: '拖动任意片段整体平移该轨道',
    shortcut: 'A',
    icon: MousePointerClick,
  },
  {
    id: 'ripple',
    label: '波纹编辑工具',
    hint: '拖边缘裁剪并自动闭合后续空隙，拖片段整体波纹位移',
    shortcut: 'B',
    icon: Waves,
  },
  {
    id: 'rolling',
    label: '滚动编辑工具',
    hint: '拖片段边缘移动与相邻片段的切点，序列总长不变',
    shortcut: 'N',
    icon: GitCommitHorizontal,
  },
  {
    id: 'rate',
    label: '比率拉伸工具',
    hint: '拖片段左右边缘改变播放速率与时长的 0.25×–4×',
    shortcut: 'R',
    icon: Gauge,
  },
  {
    id: 'razor',
    label: '刀片工具',
    hint: '点击片段在指针处拆分',
    shortcut: 'C',
    icon: Scissors,
  },
  {
    id: 'slip',
    label: '滑移工具',
    hint: '左右拖动片段改变素材入出点',
    shortcut: 'Y',
    icon: MoveHorizontal,
  },
  {
    id: 'slide',
    label: '幻灯片工具',
    hint: '拖片段主体移动，同时裁前一段尾部与后一段头部，总长不变',
    shortcut: 'U',
    icon: ChevronsLeftRight,
  },
  {
    id: 'marker',
    label: '标记工具',
    hint: '点击时间标尺添加标记',
    shortcut: 'P',
    icon: Flag,
  },
];
