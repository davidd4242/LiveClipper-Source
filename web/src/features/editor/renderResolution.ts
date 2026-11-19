/**
 * 导出分辨率选择（D75/D76）。
 *
 * 渲染器只降不升：`EditorRenderService.cs:960` `height = min(源高, plan.Resolution)`。
 * 所以对话框必须
 *   1. 默认选到「主素材还能承受的最高档」，而不是沿用上一次保存的 720（那会让 1080p 工程默认导出 720p）；
 *   2. 把超过源高的档位置灰，不让用户选一个必然被静默降级的档。
 */

export const RENDER_RESOLUTIONS = [480, 720, 1080, 2160] as const;

export type RenderResolution = (typeof RENDER_RESOLUTIONS)[number];

/** 主素材高度未知（0/NaN）时按 1080 处理：宁可保守，也不要在不知道源多高时默认导 2160。 */
export function resolutionCap(sourceHeight: number | null | undefined): number {
  return Number.isFinite(sourceHeight) && (sourceHeight ?? 0) > 0 ? Math.floor(sourceHeight as number) : 1080;
}

/** 主素材能真正输出的最高档；源比 480 还小时退到 480（渲染器侧仍按 min() 收敛）。 */
export function defaultResolution(sourceHeight: number | null | undefined): RenderResolution {
  const cap = resolutionCap(sourceHeight);
  const usable = RENDER_RESOLUTIONS.filter((value) => value <= cap);
  return (usable[usable.length - 1] ?? RENDER_RESOLUTIONS[0]) as RenderResolution;
}

/** 某一档是否会在当前主素材上被静默降级。 */
export function isResolutionDowngraded(value: number, sourceHeight: number | null | undefined): boolean {
  return value > resolutionCap(sourceHeight);
}

/** 对话框里那行提示：说清实际输出与限制来源。 */
export function resolutionHint(value: number, sourceHeight: number | null | undefined): string {
  const cap = resolutionCap(sourceHeight);
  const effective = Math.min(value, cap);
  if (sourceHeight && sourceHeight > 0 && effective < value) {
    return `实际输出 ${effective}p：主素材只有 ${cap}p，渲染只降不升`;
  }
  return `实际输出 ${effective}p`;
}
