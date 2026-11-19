/** 设计层内部工具：拼接真值 class 名。 */
export function cn(...values: Array<string | false | null | undefined>): string {
  return values.filter(Boolean).join(' ');
}
