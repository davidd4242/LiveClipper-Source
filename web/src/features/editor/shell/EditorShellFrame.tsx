import type { ReactNode } from 'react';
import './EditorShellFrame.css';
import './timelineSkin.css';
import './workspaceP1b.css';
import './a11yOverrides.css';

export interface EditorShellFrameProps {
  /** 额外类名（沿用 EditorPage 原有的 ai-open / inspector-open 状态类）。 */
  className?: string;
  children: ReactNode;
}

/**
 * 编辑器外壳根节点（P0）。
 *
 * - 外壳与画布统一使用 design/ui/tokens.css 的暗色令牌。
 * - 只做容器与作用域，不接管布局：header / .editor-body / footer 仍由
 *   EditorPage 按原结构渲染。
 */
export function EditorShellFrame({ className = '', children }: EditorShellFrameProps) {
  const classes = ['editor-shell', 'ed-shell', className].filter(Boolean).join(' ');
  return (
    <div className={classes} data-canvas-theme="dark">
      <a className="ed-skip-link" href="#editor-main">
        跳到主内容
      </a>
      {children}
    </div>
  );
}
