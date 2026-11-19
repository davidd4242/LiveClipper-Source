import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import { runInNewContext } from 'node:vm';
import { gradeCss } from './colorGrade.ts';
import { channelMixParams, cssChainMatrix, itemColorMatrix } from './engineColor.ts';
import type { ColorMatrix } from './engineColor.ts';

type RGB = [number, number, number];
const tolerance = 1e-9;
const vectors: RGB[] = [[0, .5, 1], [1, 0, .5], [.5, 1, 0]];
const unit: ColorMatrix = { m: [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0]] };
const grade = { brightness: .2, contrast: 1.1, saturation: 1.3, temperature: .4 };
const hostChain = 'brightness(1.200) contrast(1.100) saturate(1.300) sepia(0.100)';
// sepia(.1) * saturate(1.3) * contrast(1.1) * brightness(1.2), with normalized constants.
const hostMatrix: ColorMatrix = { m: [
  [1.5245301852, -.161117814, .0029196288, -.051755],
  [-.0261718644, 1.373229858, -.0002619936, -.051015],
  [-.0371414076, -.189721818, 1.5385472256, -.049685],
] };

function close(actual: number[], expected: number[], label: string) {
  assert.equal(actual.length, expected.length, label);
  actual.forEach((value, i) => assert.ok(Number.isFinite(value) && Math.abs(value - expected[i]) <= tolerance,
    `${label}[${i}]: actual=${value}, expected=${expected[i]}`));
}

function matrixClose(actual: ColorMatrix | null, expected: ColorMatrix) {
  assert.ok(actual, 'expected a matrix');
  assert.equal(actual.m.length, 3);
  actual.m.forEach((row, i) => close(row, expected.m[i], `row ${i}`));
}

function apply(matrix: ColorMatrix, vector: RGB): number[] {
  return matrix.m.map(([r, g, b, offset]) => r * vector[0] + g * vector[1] + b * vector[2] + offset);
}

const primitives: Array<{ chain: string; expected: RGB[] }> = [
  { chain: 'grayscale(1)', expected: [[.4298, .4298, .4298], [.2487, .2487, .2487], [.8215, .8215, .8215]] },
  { chain: 'sepia(1)', expected: [[.5735, .511, .398], [.4875, .433, .3375], [.9655, .8605, .67]] },
  { chain: 'sepia(0.5)', expected: [[.28675, .5055, .699], [.74375, .2165, .41875], [.73275, .93025, .335]] },
  { chain: 'saturate(0)', expected: [[.4295, .4295, .4295], [.249, .249, .249], [.8215, .8215, .8215]] },
  { chain: 'saturate(2)', expected: [[-.4295, .5705, 1.5705], [1.751, -.249, .751], [.1785, 1.1785, -.8215]] },
  { chain: 'hue-rotate(90deg)', expected: [[1, .2165, .859], [.5, .2505, -.502], [0, 1.033, 1.143]] },
  { chain: 'brightness(1.2)', expected: [[0, .6, 1.2], [1.2, 0, .6], [.6, 1.2, 0]] },
  { chain: 'contrast(2)', expected: [[-.5, .5, 1.5], [1.5, -.5, .5], [.5, 1.5, -.5]] },
];

describe('1. W3C primitive reference vectors', () => {
  for (const entry of primitives) {
    vectors.forEach((vector, index) => {
      test(`${entry.chain} on [${vector.join(',')}]`, () => {
        close(apply(cssChainMatrix(entry.chain), vector), entry.expected[index], entry.chain);
      });
    });
  }

  test('CSS percentages, omitted defaults, angle units, repeated functions and amount bounds', () => {
    matrixClose(cssChainMatrix('sepia(50%)'), cssChainMatrix('sepia(.5)'));
    matrixClose(cssChainMatrix('grayscale(200%)'), cssChainMatrix('grayscale(1)'));
    matrixClose(cssChainMatrix('sepia(2)'), cssChainMatrix('sepia(1)'));
    matrixClose(cssChainMatrix('saturate(200%)'), cssChainMatrix('saturate(2)'));
    matrixClose(cssChainMatrix('hue-rotate(.25turn)'), cssChainMatrix('hue-rotate(90deg)'));
    matrixClose(cssChainMatrix(`hue-rotate(${Math.PI / 2}rad)`), cssChainMatrix('hue-rotate(90deg)'));
    matrixClose(cssChainMatrix('hue-rotate(100grad)'), cssChainMatrix('hue-rotate(90deg)'));
    matrixClose(cssChainMatrix('hue-rotate(-270deg)'), cssChainMatrix('hue-rotate(90deg)'));
    matrixClose(cssChainMatrix('brightness() contrast() saturate() hue-rotate(0)'), unit);
    matrixClose(cssChainMatrix('sepia()'), cssChainMatrix('sepia(1)'));
    matrixClose(cssChainMatrix('grayscale()'), cssChainMatrix('grayscale(1)'));
    matrixClose(cssChainMatrix('  BRIGHTNESS( +1.2e0 )\nbrightness(5e-1)  '), cssChainMatrix('brightness(.6)'));
  });

  test('unsupported filters and malformed chains fail instead of silently dropping input', () => {
    for (const chain of ['blur(2px)', 'url(example)', 'brightness(1) junk', 'junk brightness(1)',
      'brightness(1), contrast(2)', 'brightness(1', 'brightness(1 2)', 'contrast(-1)',
      'saturate(-1%)', 'sepia(-.1)', 'hue-rotate(90)', 'hue-rotate(NaNdeg)',
      'brightness(Infinity)', 'brightness(1e309)', 'brightness(1.)', 'none brightness(1)']) {
      assert.throws(() => cssChainMatrix(chain), { name: /^(SyntaxError|RangeError)$/ }, chain);
    }
  });
});

describe('2. Order and affine constants', () => {
  test('brightness then contrast is 2.4 * RGB - .5', () => {
    const matrix = cssChainMatrix('brightness(1.2) contrast(2)');
    matrixClose(matrix, { m: [[2.4, 0, 0, -.5], [0, 2.4, 0, -.5], [0, 0, 2.4, -.5]] });
    const expected = [[-.5, .7, 1.9], [1.9, -.5, .7], [.7, 1.9, -.5]];
    vectors.forEach((vector, index) => close(apply(matrix, vector), expected[index], 'brightness then contrast'));
  });

  test('contrast then brightness is 2.4 * RGB - .6', () => {
    const matrix = cssChainMatrix('contrast(2) brightness(1.2)');
    matrixClose(matrix, { m: [[2.4, 0, 0, -.6], [0, 2.4, 0, -.6], [0, 0, 2.4, -.6]] });
    const expected = [[-.6, .6, 1.8], [1.8, -.6, .6], [.6, 1.8, -.6]];
    vectors.forEach((vector, index) => close(apply(matrix, vector), expected[index], 'contrast then brightness'));
  });

  test('the orders differ, and channel mixing also transforms the earlier offset', () => {
    assert.notDeepEqual(cssChainMatrix('brightness(1.2) contrast(2)'), cssChainMatrix('contrast(2) brightness(1.2)'));
    const mixed = cssChainMatrix('contrast(2) sepia(1)');
    close(mixed.m.map((row) => row[3]), [-.6755, -.6015, -.4685], 'sepia transforms contrast offset');
  });
});

describe('3. Real host chains and preset order', () => {
  test('gradeCss emits the real sample and its matrix equals the hand-expanded product', () => {
    const chain = gradeCss(grade);
    assert.equal(chain, hostChain);
    assert.ok(chain);
    matrixClose(cssChainMatrix(chain), hostMatrix);
    matrixClose(itemColorMatrix({ grade }), hostMatrix);
  });

  test('the real sample agrees with manually evaluated RGB values', () => {
    const expected = [[-.1293942782, .6353379354, 1.3940013166],
      [1.4742349996, -.0773178612, .6824472052], [.5493922786, 1.3091289258, -.2579775218]];
    const matrix = cssChainMatrix(gradeCss(grade) ?? '');
    vectors.forEach((vector, index) => close(apply(matrix, vector), expected[index], 'real host sample'));
  });

  const presetCases = [
    ['warm', 'sepia(.22) saturate(1.12)'],
    ['cool', 'hue-rotate(-12deg) saturate(.92)'],
    ['mono', 'grayscale(1)'],
    ['vivid', 'saturate(1.35) contrast(1.06)'],
  ];
  for (const [filter, css] of presetCases) {
    test(`${filter} uses the host preset`, () => matrixClose(itemColorMatrix({ filter }), cssChainMatrix(css)));
    test(`${filter} runs before the real grade chain`, () => {
      const matrix = itemColorMatrix({ filter, grade });
      matrixClose(matrix, cssChainMatrix(`${css} ${hostChain}`));
      assert.notDeepEqual(matrix, cssChainMatrix(`${hostChain} ${css}`));
    });
  }

  test('negative temperature retains hue rotation, second saturation and host rounding', () => {
    const negative = { brightness: .20049, contrast: 1.10049, saturation: 1.30049, temperature: -.40049 };
    const expected = 'brightness(1.200) contrast(1.100) saturate(1.300) hue-rotate(-4.81deg) saturate(0.968)';
    assert.equal(gradeCss(negative), expected);
    matrixClose(itemColorMatrix({ grade: negative }), cssChainMatrix(expected));
  });

  test('normalization and unknown preset fallback match the host helpers', () => {
    const outside = { brightness: 10, contrast: -10, saturation: 10, temperature: -10 };
    const expected = 'brightness(2.000) contrast(0.000) saturate(3.000) hue-rotate(-12.00deg) saturate(0.920)';
    assert.equal(gradeCss(outside), expected);
    matrixClose(itemColorMatrix({ filter: 'unknown', grade: outside }), cssChainMatrix(expected));
    matrixClose(itemColorMatrix({ filter: 'constructor' }), unit);
    matrixClose(itemColorMatrix({ filter: 'toString' }), unit);
  });
});

describe('4. Real channel_mix catalog and engine parameters', () => {
  const catalogSource = readFileSync(new URL('../../../../filmcraft-preview/wwwroot/js/fx-catalog.js', import.meta.url), 'utf8');
  const catalog = JSON.parse(catalogSource.slice(catalogSource.indexOf('{'), catalogSource.lastIndexOf('}') + 1)) as {
    effects: Array<{ id: string; params: Array<{ id: string; kind: string; def: number | boolean }> }>;
  };
  const channelMix = catalog.effects.find((effect) => effect.id === 'channel_mix');
  assert.ok(channelMix, 'the real engine catalog must declare channel_mix');

  test('every output key exists and the full key set equals the actual catalog', () => {
    const params = channelMixParams(hostMatrix);
    const catalogKeys = channelMix.params.map((param) => param.id);
    for (const key of Object.keys(params)) assert.ok(catalogKeys.includes(key), `unknown engine key: ${key}`);
    assert.deepEqual(Object.keys(params).sort(), [...catalogKeys].sort());
    assert.equal(Object.keys(params).length, 13);
  });

  test('all twelve row-major entries, including normalized offsets, are scaled by 100', () => {
    const matrix: ColorMatrix = { m: [[1, 2, 3, .04], [5, 6, 7, -.08], [9, 10, 11, .12]] };
    assert.deepEqual(channelMixParams(matrix), {
      rr: 100, rg: 200, rb: 300, rc: 4,
      gr: 500, gg: 600, gb: 700, gc: -8,
      br: 900, bg: 1000, bb: 1100, bc: 12, monochrome: 0,
    });
  });

  test('identity parameters equal the real catalog defaults, with numeric false for monochrome', () => {
    const defaults = Object.fromEntries(channelMix.params.map((param) => [param.id, Number(param.def)]));
    assert.deepEqual(channelMixParams(unit), defaults);
  });

  test('the actual engine applies row mapping, percent offsets, final clamp and untouched alpha', () => {
    const window: { FX?: {
      has(id: string): boolean;
      apply(data: Uint8ClampedArray, width: number, height: number, id: string, params: Record<string, number>): Uint8ClampedArray;
    } } = {};
    const warnings: unknown[][] = [];
    const source = readFileSync(new URL('../../../../filmcraft-preview/wwwroot/js/fx-engine.js', import.meta.url), 'utf8');
    runInNewContext(source, { window, console: { warn: (...args: unknown[]) => warnings.push(args) } }, { filename: 'fx-engine.js' });
    assert.ok(window.FX);
    assert.equal(window.FX.has('channel_mix'), true);
    const data = new Uint8ClampedArray([20, 100, 200, 17, 250, 0, 20, 180]);
    const matrix: ColorMatrix = { m: [[0, 1, 0, .12], [0, 0, .5, -.07], [1, 0, 0, .2]] };
    window.FX.apply(data, 2, 1, 'channel_mix', channelMixParams(matrix));
    assert.deepEqual([...data], [131, 82, 71, 17, 31, 0, 255, 180]);
    assert.deepEqual(warnings, []);
  });

  test('malformed or non-finite matrices fail before producing engine parameters', () => {
    for (const matrix of [{ m: [] }, { m: [[1, 0, 0], [0, 1, 0], [0, 0, 1]] },
      { m: [[NaN, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0]] },
      { m: [[Infinity, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0]] }]) {
      assert.throws(() => channelMixParams(matrix), /finite 3x4 color matrix/);
    }
  });
});

describe('5. Identity and the host default predicate', () => {
  test('an empty, whitespace-only or none CSS chain returns an identity matrix', () => {
    for (const chain of ['', ' \n\t ', 'none', ' NONE ']) matrixClose(cssChainMatrix(chain), unit);
  });

  test('default items return null for absent, null, empty and explicit grades', () => {
    for (const item of [{}, { filter: null }, { filter: 'none' }, { grade: null }, { grade: {} },
      { grade: { brightness: 0, contrast: 1, saturation: 1, temperature: 0 } },
      { grade: false }, { grade: 0 }, { grade: 'historical-invalid-value' }]) {
      assert.equal(itemColorMatrix(item), null);
    }
  });

  test('the host sub-threshold grades remain no-effect items', () => {
    const nearDefault = { brightness: .0009, contrast: 1.0009, saturation: .9991, temperature: -.0009 };
    assert.equal(gradeCss(nearDefault), undefined);
    assert.equal(itemColorMatrix({ grade: nearDefault }), null);
  });

  test('exact threshold preserves the host predicate even when gradeCss emits no operator', () => {
    const atThreshold = { brightness: .001 };
    assert.equal(gradeCss(atThreshold), undefined);
    matrixClose(itemColorMatrix({ grade: atThreshold }), unit);
  });

  test('matrices are independent values and parameter conversion leaves the input unchanged', () => {
    const a = cssChainMatrix('');
    a.m[0][0] = 8;
    matrixClose(cssChainMatrix(''), unit);
    const matrix = cssChainMatrix(hostChain);
    const before = structuredClone(matrix);
    channelMixParams(matrix);
    assert.deepEqual(matrix, before);
  });
});
