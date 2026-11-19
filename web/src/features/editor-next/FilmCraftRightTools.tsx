import type { LucideIcon } from 'lucide-react';
import {
  Captions,
  ListChecks,
  Music,
  Scissors,
  Sparkles,
} from 'lucide-react';
import './FilmCraftRightTools.css';

export type FilmCraftRightTool =
  | 'transcript'
  | 'analysis'
  | 'events'
  | 'rough-cut'
  | 'music';

export interface FilmCraftRightToolsProps {
  active: FilmCraftRightTool | null;
  onSelect: (tool: FilmCraftRightTool) => void;
  disabled?: boolean;
  sourceUnavailable?: string;
}

const RIGHT_TOOLS: ReadonlyArray<{
  tool: FilmCraftRightTool;
  label: string;
  Icon: LucideIcon;
}> = [
  { tool: 'transcript', label: '转写字幕', Icon: Captions },
  { tool: 'analysis', label: 'AI分析', Icon: Sparkles },
  { tool: 'events', label: '选择事件', Icon: ListChecks },
  { tool: 'rough-cut', label: 'AI粗剪', Icon: Scissors },
  { tool: 'music', label: '音乐', Icon: Music },
];

export function FilmCraftRightTools(props: FilmCraftRightToolsProps) {
  return (
    <div className="fc-right-tools" role="group" aria-label="剪辑工具">
      {RIGHT_TOOLS.map(({ tool, label, Icon }) => {
        const isActive = props.active === tool;
        return (
          <button
            key={tool}
            type="button"
            className={isActive ? 'fc-right-tool is-active' : 'fc-right-tool'}
            title={tool !== 'music' && props.sourceUnavailable ? props.sourceUnavailable : label}
            aria-label={label}
            aria-pressed={isActive}
            disabled={props.disabled === true || (tool !== 'music' && !!props.sourceUnavailable)}
            onClick={() => props.onSelect(tool)}
          >
            <Icon aria-hidden="true" />
            <span className="fc-right-tool-label">{label}</span>
          </button>
        );
      })}
    </div>
  );
}
