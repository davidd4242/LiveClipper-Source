import type { ColorGrade } from '../../api/types';

/** 调色参数的“不动作”默认值：亮度 0、对比度/饱和度 1（倍率）、色温 0。 */
export interface NormalizedGrade {
  brightness: number;
  contrast: number;
  saturation: number;
  temperature: number;
}

export const DEFAULT_GRADE: NormalizedGrade = { brightness: 0, contrast: 1, saturation: 1, temperature: 0 };

const clamp = (value: number, min: number, max: number) =>
  Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : min;

/** 读入任意历史/半成品数据，越界与非有限值一律收敛到合法区间。 */
export function normalizeGrade(grade: ColorGrade | null | undefined): NormalizedGrade {
  if (!grade) return { ...DEFAULT_GRADE };
  return {
    brightness: clamp(grade.brightness ?? 0, -1, 1),
    contrast: clamp(grade.contrast ?? 1, 0, 3),
    saturation: clamp(grade.saturation ?? 1, 0, 3),
    temperature: clamp(grade.temperature ?? 0, -1, 1),
  };
}

export function gradeIsDefault(grade: ColorGrade | null | undefined): boolean {
  const value = normalizeGrade(grade);
  return (
    Math.abs(value.brightness) < 0.001 &&
    Math.abs(value.contrast - 1) < 0.001 &&
    Math.abs(value.saturation - 1) < 0.001 &&
    Math.abs(value.temperature) < 0.001
  );
}

/** 预览用 CSS filter 片段；参数全部为默认值时不产生任何滤镜。 */
export function gradeCss(grade: ColorGrade | null | undefined): string | undefined {
  if (!grade || gradeIsDefault(grade)) return undefined;
  const value = normalizeGrade(grade);
  const parts: string[] = [];
  if (Math.abs(value.brightness) > 0.001) parts.push(`brightness(${(1 + value.brightness).toFixed(3)})`);
  if (Math.abs(value.contrast - 1) > 0.001) parts.push(`contrast(${value.contrast.toFixed(3)})`);
  if (Math.abs(value.saturation - 1) > 0.001) parts.push(`saturate(${value.saturation.toFixed(3)})`);
  if (value.temperature > 0.001) parts.push(`sepia(${(value.temperature * 0.25).toFixed(3)})`);
  if (value.temperature < -0.001) {
    parts.push(`hue-rotate(${(value.temperature * 12).toFixed(2)}deg)`);
    parts.push(`saturate(${(1 + value.temperature * 0.08).toFixed(3)})`);
  }
  return parts.length > 0 ? parts.join(' ') : undefined;
}

/** 叠加到已有滤镜字符串（预设滤镜在前，调色在后）。 */
export function combineFilters(...values: Array<string | undefined>): string | undefined {
  const parts = values.filter((value): value is string => Boolean(value && value.trim()));
  return parts.length > 0 ? parts.join(' ') : undefined;
}

/** 界面滑杆 → 业务值：亮度/色温按 -100..100 显示，对比度/饱和度按 0..300 显示。 */
export function gradeToSliders(grade: ColorGrade | null | undefined) {
  const value = normalizeGrade(grade);
  return {
    brightness: Math.round(value.brightness * 100),
    contrast: Math.round(value.contrast * 100),
    saturation: Math.round(value.saturation * 100),
    temperature: Math.round(value.temperature * 100),
  };
}

export function slidersToGrade(sliders: {
  brightness: number;
  contrast: number;
  saturation: number;
  temperature: number;
}): NormalizedGrade {
  return normalizeGrade({
    brightness: sliders.brightness / 100,
    contrast: sliders.contrast / 100,
    saturation: sliders.saturation / 100,
    temperature: sliders.temperature / 100,
  });
}
