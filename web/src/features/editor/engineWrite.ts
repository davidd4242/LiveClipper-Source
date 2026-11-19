import type { ClipPatch, EngineAdapter, EngineClipEffect, EngineClipTransition, EngineResult } from '../../engine';

/**
 * 右栏 → 引擎的单向写桥（W3-wire / D4 / D54）。
 *
 * 只做三件事：把宿主 patch 归一成引擎合法 patch、调一次 `updateClip`、把结果如实返回。
 * 不缓存 clip 状态、不重试、不猜字段——右栏的真相源仍然是 `useEditorSession()` 的 items。
 */

/** 宿主字段 → 引擎字段。这张表就是全部支持面；不在这里的一律进 `dropped`。 */
const FIELD_MAP = {
  /** 标题文字就是引擎里的片段名。 */
  titleText: 'name',
  /** 宿主的 source in-point（秒）在引擎里叫 sourceIn。 */
  start: 'sourceIn',
  speed: 'speed',
} as const;

export type EngineWriteResult = Readonly<{
  ok: boolean;
  /** 真正发给引擎的 patch；为空对象表示没有任何字段可映射，未调用引擎。 */
  sent: ClipPatch;
  /** 宿主给了、但引擎没有对应写入口的字段名。 */
  dropped: readonly string[];
  code?: string;
  message?: string;
}>;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/**
 * 宿主 patch → 引擎 patch。纯函数，便于逐字段断言。
 * 注意 `enabled` 是取反：宿主用 `disabled` 表达。
 */
export function toClipPatch(host: Record<string, unknown>): { patch: ClipPatch; dropped: string[] } {
  const patch: Record<string, unknown> = {};
  const dropped: string[] = [];
  for (const [field, value] of Object.entries(host)) {
    if (value === undefined) continue;
    if (field === 'disabled') {
      if (typeof value === 'boolean') patch.enabled = !value;
      else dropped.push(field);
      continue;
    }
    if (field === 'effects') {
      if (Array.isArray(value)) patch.effects = value as EngineClipEffect[];
      else dropped.push(field);
      continue;
    }
    if (field === 'transition') {
      // null 是合法的"清除转场"；其余必须是对象，交给引擎自己校验形状。
      if (value === null || isRecord(value)) patch.transition = value as EngineClipTransition;
      else dropped.push(field);
      continue;
    }
    const engineField = FIELD_MAP[field as keyof typeof FIELD_MAP];
    if (!engineField) { dropped.push(field); continue; }
    if (engineField === 'name') {
      if (typeof value === 'string' || value === null) patch.name = value ?? '';
      else dropped.push(field);
      continue;
    }
    if (typeof value === 'number' && Number.isFinite(value)) patch[engineField] = value;
    else dropped.push(field);
  }
  return { patch: patch as ClipPatch, dropped };
}

/**
 * 把一次右栏编辑推给引擎。引擎报错时原样返回它的 `code`/`message`——
 * 调用方负责提示用户，这里不吞错、不重试。
 */
export async function applyInspectorEdit(
  adapter: Pick<EngineAdapter, 'updateClip'>,
  clipKey: string,
  host: Record<string, unknown>,
): Promise<EngineWriteResult> {
  const { patch, dropped } = toClipPatch(host);
  if (!clipKey) return { ok: false, sent: {}, dropped, code: 'missing_clip_key', message: '没有选中片段，无法写入引擎。' };
  if (Object.keys(patch).length === 0) return { ok: true, sent: {}, dropped };
  let result: EngineResult;
  try {
    result = await adapter.updateClip(clipKey, patch);
  } catch (reason) {
    return { ok: false, sent: patch, dropped, code: 'engine_write_threw', message: String(reason) };
  }
  if (!result?.ok) {
    return {
      ok: false, sent: patch, dropped,
      code: result?.code ?? 'engine_write_failed',
      message: result?.message ?? result?.error ?? '引擎拒绝了这次写入。',
    };
  }
  return { ok: true, sent: patch, dropped };
}
