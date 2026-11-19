import type { ReactNode } from 'react';
import { Captions, ListTree, Music2, SlidersHorizontal, Sparkles } from 'lucide-react';
import type { AiTab } from './AiDrawer';

interface AiRailProps {
  open: boolean;
  activeTab: AiTab;
  counts: { candidates: number; transcript: number; chapters: number; music: number };
  inspectorOpen: boolean;
  onToggle: () => void;
  onSelectTab: (tab: AiTab) => void;
  onToggleInspector: () => void;
}

export function AiRail({
  open,
  activeTab,
  counts,
  inspectorOpen,
  onToggle,
  onSelectTab,
  onToggleInspector,
}: AiRailProps) {
  const tabs: Array<{ id: AiTab; label: string; icon: ReactNode; count: number }> = [
    { id: 'candidates', label: 'AI 候选', icon: <Sparkles size={14} />, count: counts.candidates },
    { id: 'transcript', label: '转写', icon: <Captions size={14} />, count: counts.transcript },
    { id: 'chapters', label: '章节', icon: <ListTree size={14} />, count: counts.chapters },
    { id: 'music', label: '配乐', icon: <Music2 size={14} />, count: counts.music },
  ];

  return (
    <aside className="ai-rail" aria-label="AI 工具">
      <button
        type="button"
        className={`ai-rail-brand${open ? ' is-open' : ''}`}
        onClick={onToggle}
        aria-label={open ? '收起 AI 助手' : '展开 AI 助手'}
        aria-expanded={open}
      >
        <span className="ai-rail-logo">
          <Sparkles size={14} />
        </span>
        <span>AI</span>
      </button>

      <div className="ai-rail-tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={`ai-rail-tab${open && activeTab === tab.id ? ' is-active' : ''}`}
            onClick={() => onSelectTab(tab.id)}
            aria-pressed={open && activeTab === tab.id}
          >
            {tab.icon}
            <span>{tab.label}</span>
            {tab.count > 0 ? <em>{tab.count}</em> : null}
          </button>
        ))}
      </div>
      <div className="ai-rail-spacer" />
      <button
        type="button"
        className={`ai-rail-tab ai-rail-inspector${inspectorOpen ? ' is-active' : ''}`}
        onClick={onToggleInspector}
        aria-label={inspectorOpen ? '收起属性面板' : '展开属性面板'}
        aria-expanded={inspectorOpen}
      >
        <SlidersHorizontal size={14} />
        <span>属性</span>
      </button>
    </aside>
  );
}
