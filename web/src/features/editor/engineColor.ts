import { gradeCss, gradeIsDefault } from './colorGrade.ts';

/** Encoded RGB, with normalized (0..1) offsets in the fourth column. */
export interface ColorMatrix { m: number[][]; }

const identity = (): ColorMatrix => ({ m: [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0]] });
const numberPattern = '[+-]?(?:\\d*\\.\\d+|\\d+)(?:e[+-]?\\d+)?';
const scalarPattern = new RegExp(`^(${numberPattern})(%)?$`, 'i');
const anglePattern = new RegExp(`^(${numberPattern})(deg|rad|grad|turn)?$`, 'i');

function scalar(value: string): number {
  if (!value) return 1;
  const match = scalarPattern.exec(value);
  if (!match) throw new SyntaxError(`Invalid CSS filter amount: ${value}`);
  const result = Number(match[1]) / (match[2] ? 100 : 1);
  if (!Number.isFinite(result) || result < 0) throw new RangeError(`Invalid CSS filter amount: ${value}`);
  return result;
}

function radians(value: string): number {
  if (!value) return 0;
  const match = anglePattern.exec(value);
  if (!match) throw new SyntaxError(`Invalid CSS filter angle: ${value}`);
  const amount = Number(match[1]);
  if (!Number.isFinite(amount)) throw new RangeError(`Invalid CSS filter angle: ${value}`);
  if (!match[2] && amount !== 0) throw new SyntaxError(`CSS filter angle requires a unit: ${value}`);
  const unit = (match[2] ?? 'deg').toLowerCase();
  const period = unit === 'turn' ? 1 : unit === 'rad' ? 2 * Math.PI : unit === 'grad' ? 400 : 360;
  return (amount % period) / period * 2 * Math.PI;
}

function primitive(name: string, value: string): ColorMatrix {
  // Filter Effects 1: grayscale uses .2126/.7152/.0722; saturate and hue use .213/.715/.072.
  if (name === 'hue-rotate') {
    const angle = radians(value), c = Math.cos(angle), s = Math.sin(angle);
    return { m: [
      [.213 + .787 * c - .213 * s, .715 - .715 * c - .715 * s, .072 - .072 * c + .928 * s, 0],
      [.213 - .213 * c + .143 * s, .715 + .285 * c + .140 * s, .072 - .072 * c - .283 * s, 0],
      [.213 - .213 * c - .787 * s, .715 - .715 * c + .715 * s, .072 + .928 * c + .072 * s, 0],
    ] };
  }
  if (!['brightness', 'contrast', 'saturate', 'sepia', 'grayscale'].includes(name)) {
    throw new SyntaxError(`Unsupported CSS filter: ${name}`);
  }
  const k = scalar(value);
  switch (name) {
    case 'brightness':
      return { m: [[k, 0, 0, 0], [0, k, 0, 0], [0, 0, k, 0]] };
    case 'contrast': {
      const offset = .5 - .5 * k;
      return { m: [[k, 0, 0, offset], [0, k, 0, offset], [0, 0, k, offset]] };
    }
    case 'saturate':
      return { m: [
        [.213 + .787 * k, .715 - .715 * k, .072 - .072 * k, 0],
        [.213 - .213 * k, .715 + .285 * k, .072 - .072 * k, 0],
        [.213 - .213 * k, .715 - .715 * k, .072 + .928 * k, 0],
      ] };
    case 'sepia': {
      const a = Math.min(1, k);
      return { m: [
        [1 - .607 * a, .769 * a, .189 * a, 0],
        [.349 * a, 1 - .314 * a, .168 * a, 0],
        [.272 * a, .534 * a, 1 - .869 * a, 0],
      ] };
    }
    default: {
      const a = Math.min(1, k), t = 1 - a;
      return { m: [
        [t + .2126 * a, .7152 * a, .0722 * a, 0],
        [.2126 * a, t + .7152 * a, .0722 * a, 0],
        [.2126 * a, .7152 * a, t + .0722 * a, 0],
      ] };
    }
  }
}

function compose(after: ColorMatrix, before: ColorMatrix): ColorMatrix {
  return { m: after.m.map((row) => Array.from({ length: 4 }, (_, col) =>
    row[0] * before.m[0][col] + row[1] * before.m[1][col] + row[2] * before.m[2][col]
    + (col === 3 ? row[3] : 0),
  )) };
}

/** Composes affine operators left to right, retaining out-of-range intermediate RGB. */
export function cssChainMatrix(chain: string): ColorMatrix {
  const input = chain.trim();
  let result = identity();
  if (!input || input.toLowerCase() === 'none') return result;
  const filter = /([a-z-]+)\(\s*([^()]*)\)/iy;
  let cursor = 0;
  while (cursor < input.length) {
    while (/\s/.test(input[cursor] ?? '')) cursor++;
    if (cursor === input.length) break;
    filter.lastIndex = cursor;
    const match = filter.exec(input);
    if (!match) throw new SyntaxError(`Invalid CSS filter chain at: ${input.slice(cursor)}`);
    result = compose(primitive(match[1].toLowerCase(), match[2].trim()), result);
    cursor = filter.lastIndex;
  }
  return result;
}

const presets = new Map([
  ['warm', 'sepia(.22) saturate(1.12)'],
  ['cool', 'hue-rotate(-12deg) saturate(.92)'],
  ['mono', 'grayscale(1)'],
  ['vivid', 'saturate(1.35) contrast(1.06)'],
]);

export function itemColorMatrix(item: { filter?: string | null; grade?: unknown }): ColorMatrix | null {
  const grade = item.grade as Parameters<typeof gradeCss>[0];
  const filter = item.filter ?? 'none';
  if (gradeIsDefault(grade) && filter === 'none') return null;
  // Calling the host helper preserves its normalization, thresholds and decimal formatting.
  return cssChainMatrix([presets.get(filter), gradeCss(grade)].filter(Boolean).join(' '));
}

export function channelMixParams(matrix: ColorMatrix): Record<string, number> {
  if (matrix.m.length !== 3 || matrix.m.some((row) => row.length !== 4 || row.some((value) => !Number.isFinite(value)))) {
    throw new RangeError('Expected a finite 3x4 color matrix');
  }
  // fx-catalog.js:2482-2567; fx-engine.js:491-497 divides every entry (including constants) by 100.
  // Rows [r,g,b,offset] map to [rr,rg,rb,rc], [gr,gg,gb,gc], [br,bg,bb,bc]; monochrome=0 keeps all rows.
  const keys = [['rr', 'rg', 'rb', 'rc'], ['gr', 'gg', 'gb', 'gc'], ['br', 'bg', 'bb', 'bc']];
  const params: Record<string, number> = {};
  matrix.m.forEach((row, r) => row.forEach((value, c) => { params[keys[r][c]] = value * 100; }));
  params.monochrome = 0;
  return params;
}

/** 一条 CSS filter 链里的函数个数（`none`/空 计 0）。 */
export function colorChainLength(item: { filter?: string | null; grade?: unknown }): number {
  const grade = item.grade as Parameters<typeof gradeCss>[0];
  const chain = [presets.get(item.filter ?? 'none'), gradeCss(grade)].filter(Boolean).join(' ').trim();
  if (!chain || chain.toLowerCase() === 'none') return 0;
  return chain.match(/[a-z-]+\s*\(/gi)?.length ?? 0;
}

/**
 * 只有**单算子**链能安全交给引擎（D65）：引擎逐效果调用 `clamp01`，把整链压成一个矩阵会偏
 * （组合链实测 maxError 16，单算子 1）。多算子返回 null，由宿主渲染兜底。
 */
export function singleOperatorEffect(item: { filter?: string | null; grade?: unknown }): Record<string, number> | null {
  if (colorChainLength(item) !== 1) return null;
  const matrix = itemColorMatrix(item);
  return matrix ? channelMixParams(matrix) : null;
}
