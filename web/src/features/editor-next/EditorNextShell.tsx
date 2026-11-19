/**
 * R81 外壳：把 `/editor-next` 重新套回应用外壳。
 *
 * 背景：`/editor-next` 在 `web/src/App.tsx` 里是裸渲染（`:65`），切过去之后
 * 全局左侧主导航（`RailFrame` / `IconRail`）、面包屑与保存态一起消失，
 * 和 `/editor`、`/studio`、`/live` 的整体 UI 断裂。
 *
 * 结构（自上而下）：RailFrame = IconRail（剪辑恒为选中，`aria-current="page"`）
 * + 48px 顶栏（返回工作台 / 剪辑 › 工程 › 序列 / 保存态回显）+ 页面槽。
 *
 * 两条硬约束决定了这里的形状：
 * 1. 页面**不 import 本文件**（多个 merge 测试用 rolldown 单独编译
 *    `EditorNextPage.tsx`，新增运行时 import 会让它们直接抛错）。外壳由
 *    `App.tsx` 以 `shell` prop 注入，测试环境没有 prop 时页面渲染树不变。
 * 2. 所有状态都由页面传入（`save.state` 已经在页面里），外壳不另建保存
 *    状态机；离开前只调页面给的 `flush`（= `useEditorNextSave().save`）。
 *    页面的 `beforeunload` / Ctrl+S / 关闭握手一律不动。
 */
import type { ComponentType, ReactNode } from 'react';
import { ChevronLeft } from 'lucide-react';
import { RailFrame } from '../../app/RailFrame';
import './r81-editor-next-shell.css';

/** 与 `editorNextSaveController` 的状态枚举一致。 */
export type EditorNextSaveStatus = 'saved' | 'unsaved' | 'saving' | 'conflict' | 'error';

export interface EditorNextShellProps {
  /** 面包屑第二段（工程名，来自已载入的 clip 标题）。 */
  projectName: string;
  /** 面包屑第三段（序列名）。 */
  sequenceName: string;
  saveStatus: EditorNextSaveStatus;
  /** 保存态文案，页面用已有的 `saveLabels` 生成。 */
  saveLabel: string;
  /** 保存态解释（title）。 */
  saveTitle: string;
  /** 离开前的落盘守卫：false = 冲突/失败，必须留在原地。 */
  flush: () => Promise<boolean>;
  children?: ReactNode;
}

export type EditorNextShellComponent = ComponentType<EditorNextShellProps>;

/** 剪辑页属于外壳的「剪辑」项，返回即回工作台。 */
const HOME_HREF = '/';

export function EditorNextShell({
  projectName,
  sequenceName,
  saveStatus,
  saveLabel,
  saveTitle,
  flush,
  children,
}: EditorNextShellProps) {
  const leave = (href: string) => {
    void flush().then((ok) => {
      // 保存没落盘就不跳：页面自己的保存提示会说明冲突/失败原因。
      if (ok) window.location.assign(href);
    });
  };

  return (
    <RailFrame beforeNavigate={() => flush()}>
      <div className="editor-next-shell">
        <a className="editor-next-shell__skip" href="#editor-next-main">跳到编辑区</a>
        <header className="editor-next-shell__bar">
          <button
            type="button"
            className="editor-next-shell__back"
            aria-label="返回工作台"
            title="返回工作台（离开前会先保存）"
            onClick={() => leave(HOME_HREF)}
          >
            <ChevronLeft size={18} aria-hidden="true" />
          </button>
          <nav className="editor-next-shell__crumbs" aria-label="当前位置">
            <button type="button" className="editor-next-shell__crumb" onClick={() => leave(HOME_HREF)}>剪辑</button>
            <span className="editor-next-shell__sep" aria-hidden="true">›</span>
            <span className="editor-next-shell__crumb-text" title={projectName}>{projectName}</span>
            <span className="editor-next-shell__sep" aria-hidden="true">›</span>
            <span className="editor-next-shell__crumb-text" aria-current="page">{sequenceName}</span>
          </nav>
          <p className="editor-next-shell__save" data-save-status={saveStatus} title={saveTitle}>
            <span className="editor-next-shell__dot" aria-hidden="true" />
            {saveLabel}
          </p>
        </header>
        <div className="editor-next-shell__body">{children}</div>
      </div>
    </RailFrame>
  );
}
