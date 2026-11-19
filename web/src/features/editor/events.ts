import type { EditorProject, StudioEvent, TimelineItem } from '../../api/types';

/**
 * 事件列表（B 端）适配层。
 *
 * 数据来源分两级：
 * 1. 真实候选：`EditorPayload.events`（后端 `/api/studio/events/extract` 产出，字段见
 *    `web/src/api/types.ts:195` 的 StudioEvent）。
 * 2. 示例数据：当前后端还没有 auto-plan 事件流（`EditPlanningService` 的
 *    CutSuggestion 尚未暴露给前端），列表为空时用工程时间线派生 3–5 条「示例」事件，
 *    只用于验证 UI 链路；示例事件带 `mock: true`，不可写入时间线。
 *
 * 字段契约（真实 auto-plan 接入时的目标形状）见 `design/ui/filmcraft-events-ui.md`：
 *   id / start / end / title / summary / hook / source / reasons[] / heat / decision
 */

/** 事件在列表里的决策状态：待定 / 采用（勾选等待应用）/ 排除。 */
export type EventDecision = 'pending' | 'adopted' | 'excluded';

/** 列表渲染用的事件视图（把 StoreEvent 补上决策/热度/理由等展示字段）。 */
export interface EditorEventView {
  id: string;
  start: number;
  end: number;
  title: string;
  summary: string;
  hook: string;
  source: string;
  /** 理由列表；auto-plan 接入前为空数组（详情面板显示「—」）。 */
  reasons: string[];
  /** 弹幕热度（0–100 或原始值）；事件 API 接入前为 null。 */
  heat: number | null;
  decision: EventDecision;
  /** 示例数据（来自工程时间线派生，不能应用）。 */
  mock: boolean;
}

export const MOCK_SOURCE = 'mock';

export function eventSourceLabel(source: string): string {
  if (source === MOCK_SOURCE) return '示例';
  if (source === 'rules') return '规则';
  if (source === 'ai') return 'AI';
  if (source === 'danmaku') return '弹幕';
  return source || '未知来源';
}

/** 从真实候选构建视图；adopted/excluded 是 EditorPage 里的共享决策集合。 */
export function buildEventViews(
  events: StudioEvent[],
  project: EditorProject | null,
  adopted: Set<string>,
  excluded: Set<string>,
): EditorEventView[] {
  if (events.length > 0) {
    return events
      .slice()
      .sort((a, b) => a.start - b.start)
      .map((event) => ({
        id: event.id,
        start: event.start,
        end: event.end,
        title: event.title,
        summary: event.summary,
        hook: event.hook,
        source: event.source,
        reasons: [],
        heat: null,
        decision: adopted.has(event.id) ? 'adopted' : excluded.has(event.id) ? 'excluded' : 'pending',
        mock: false,
      }));
  }

  // 示例数据：从工程时间线派生（不写后端、不带热度/理由）。
  const items: TimelineItem[] = project?.timeline ?? [];
  return items.slice(0, 4).map((item, index) => {
    const id = `mock-event-${index + 1}`;
    return {
      id,
      start: item.start,
      end: item.end,
      title: `示例事件 ${index + 1} · 疑似高光`,
      summary: '示例数据：接入 auto-plan / 事件 API 后替换为真实摘要（当前不可应用）。',
      hook: '',
      source: MOCK_SOURCE,
      reasons: [],
      heat: null,
      decision: adopted.has(id) ? 'adopted' : excluded.has(id) ? 'excluded' : 'pending',
      mock: true,
    } satisfies EditorEventView;
  });
}

/** 事件状态点的语义色：采用=绿、排除=红、待定=紫（与设计层 StatusDot 对齐）。 */
export function eventDotTone(decision: EventDecision): 'green' | 'red' | 'purple' {
  if (decision === 'adopted') return 'green';
  if (decision === 'excluded') return 'red';
  return 'purple';
}

export function eventDecisionLabel(decision: EventDecision): string {
  if (decision === 'adopted') return '已采用';
  if (decision === 'excluded') return '已排除';
  return '待定';
}
