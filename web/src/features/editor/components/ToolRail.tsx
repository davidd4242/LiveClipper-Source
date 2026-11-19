import {
  ChevronsLeftRight,
  Gauge,
  GitCommitHorizontal,
  Hand,
  MousePointer2,
  MousePointerClick,
  MoveHorizontal,
  PenTool,
  Scissors,
  Waves,
  ZoomIn,
} from 'lucide-react';

export type EditorTool =
  | 'select'
  | 'track'
  | 'ripple'
  | 'rolling'
  | 'rate'
  | 'razor'
  | 'slip'
  | 'slide'
  | 'pen'
  | 'hand'
  | 'zoom'
  | 'marker';

interface ToolRailProps {
  tool: EditorTool;
  onChange: (tool: EditorTool) => void;
}

const tools = [
  { id: 'select' as const, label: '选择工具：点选片段，拖动排序，拖左右边缘裁剪', shortcut: 'V', icon: MousePointer2 },
  {
    id: 'track' as const,
    label: '轨道选择工具：拖动任意片段整体平移该轨道',
    shortcut: 'A',
    icon: MousePointerClick,
  },
  {
    id: 'ripple' as const,
    label: '波纹编辑工具：拖边缘裁剪并自动闭合后续空隙，拖片段整体波纹位移',
    shortcut: 'B',
    icon: Waves,
  },
  {
    id: 'rolling' as const,
    label: '滚动编辑工具：拖片段边缘移动与相邻片段的切点，序列总长不变',
    shortcut: 'N',
    icon: GitCommitHorizontal,
  },
  {
    id: 'rate' as const,
    label: '比率拉伸工具：拖片段左右边缘改变播放速率与时长的 0.25×–4×',
    shortcut: 'R',
    icon: Gauge,
  },
  { id: 'razor' as const, label: '刀片工具：点击片段在指针处拆分', shortcut: 'C', icon: Scissors },
  { id: 'slip' as const, label: '滑移工具：左右拖动片段改变素材入出点', shortcut: 'Y', icon: MoveHorizontal },
  {
    id: 'slide' as const,
    label: '幻灯片工具：拖片段主体移动，同时裁前一段尾部与后一段头部，总长不变',
    shortcut: 'U',
    icon: ChevronsLeftRight,
  },
  {
    id: 'pen' as const,
    label: '钢笔工具：点击片段在指针处添加关键帧（v1 音量包络）',
    shortcut: 'P',
    icon: PenTool,
  },
  { id: 'hand' as const, label: '手形工具：拖动平移时间线视图', shortcut: 'H', icon: Hand },
  { id: 'zoom' as const, label: '缩放工具：点击放大时间线（Alt+点击缩小）', shortcut: 'Z', icon: ZoomIn },
];

export function ToolRail({ tool, onChange }: ToolRailProps) {
  return (
    <div className="tool-rail" role="toolbar" aria-label="时间线工具">
      {tools.map((entry) => {
        const Icon = entry.icon;
        return (
          <button
            key={entry.id}
            type="button"
            className={`tool-rail__button${tool === entry.id ? ' is-active' : ''}`}
            title={`${entry.label} (${entry.shortcut})`}
            aria-label={entry.label}
            aria-pressed={tool === entry.id}
            onClick={() => onChange(entry.id)}
          >
            <Icon size={14} strokeWidth={1.7} />
            <span className="tool-rail__key">{entry.shortcut}</span>
          </button>
        );
      })}
    </div>
  );
}
