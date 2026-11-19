/// <reference types="node" />
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setImmediate } from 'node:timers';
import test from 'node:test';
import vm from 'node:vm';
import { createEngineAdapter } from './adapter.ts';
import { loadEngine } from './loader.ts';
import type { TestContext } from 'node:test';
import { createEngineDomAdapter } from './dom-adapter.ts';
import type { FCBridge, FCBridgeClip, FCBridgeEvent } from './adapter.ts';
import type { ClipPatch, EngineClipEffect, EngineClipTransition, EngineEventMap, EngineRequest, EngineResult } from './types.ts';

function fakeBridge(fps = 25) {
  const calls = new Map<string, unknown[][]>();
  const handlers = new Map<FCBridgeEvent, Set<(value: Record<string, unknown>) => void>>();
  const failures = new Map<string, unknown>();
  const state = {
    fps, seconds: 0, playing: false, keys: [] as string[], revision: 0, dirty: false,
    clips: [{ key: 'c1', track: 'V1', start: 1, duration: 2, name: 'one', type: 'video' }] as FCBridgeClip[],
  };
  const emit = (event: FCBridgeEvent, value: Record<string, unknown>) => {
    for (const cb of [...(handlers.get(event) ?? [])]) cb(value);
  };
  const changed = () => {
    state.dirty = true;
    state.revision++;
    emit('dirty', { dirty: true, revision: state.revision });
  };
  const raw: FCBridge = {
    version: '1', ready: Promise.resolve(),
    openProject(project) {
      const sequence = (project as { sequence?: { fps?: number } }).sequence;
      if (sequence?.fps) state.fps = sequence.fps;
      return { ok: true };
    },
    openDemo() { state.seconds = 0; return { ok: true }; },
    closeProject() { state.clips = []; return { ok: true }; },
    getProjectSummary: () => ({ name: 'test', duration: 8, width: 1920, height: 1080, tracks: 2, clips: state.clips.length, fps: state.fps, revision: state.revision }),
    setPlayhead(seconds) { state.seconds = seconds; emit('playhead', { frame: Math.round(seconds * state.fps), seconds }); },
    getPlayhead: () => state.seconds,
    play() { state.playing = true; emit('playstate', { playing: true }); },
    pause() { state.playing = false; emit('playstate', { playing: false }); },
    isPlaying: () => state.playing,
    getSelection: () => [...state.keys],
    selectClips(keys) { state.keys = [...keys]; emit('selection', { keys, revision: 1 }); },
    deselectAll() { state.keys = []; emit('selection', { keys: [], revision: 2 }); },
    listClips: () => state.clips.map((clip) => ({ ...clip })),
    updateClip(key, patch) {
      const clip = state.clips.find((item) => item.key === key);
      if (!clip) return { ok: false, error: 'clip_not_found' };
      if (patch.start !== undefined) clip.start = patch.start as number;
      if (patch.duration !== undefined) clip.duration = patch.duration as number;
      if (patch.track !== undefined) clip.track = patch.track as string;
      if (patch.effects !== undefined) clip.effects = patch.effects as EngineClipEffect[];
      if (patch.transition !== undefined) clip.transition = patch.transition as EngineClipTransition;
      changed();
      return { ok: true, warnings: patch.effects ? ['unknown_effect_fixture'] : [] };
    },
    addClip(clip) { const key = `new${state.clips.length}`; state.clips.push({ ...clip, key }); changed(); return { ok: true, key }; },
    removeClips(keys) { const count = state.clips.length; state.clips = state.clips.filter((clip) => !keys.includes(clip.key)); changed(); return { ok: true, removed: count - state.clips.length }; },
    undo() { changed(); }, redo() { changed(); }, canUndo: () => true, canRedo: () => false,
    isDirty: () => state.dirty,
    clearDirty() { state.dirty = false; emit('dirty', { dirty: false, revision: state.revision }); },
    importAssets(handles) {
      const keys: string[] = [];
      let at = state.seconds;
      for (const handle of handles) {
        const key = `import-${typeof handle === 'string' ? handle : handle.assetId ?? handle.id}`;
        state.clips.push({ key, track: 'V1', start: at, duration: 1, name: key, type: 'video' });
        keys.push(key);
        at++;
      }
      changed();
      return { ok: true, keys };
    },
    on(event, cb) {
      const callbacks = handlers.get(event) ?? new Set();
      handlers.set(event, callbacks);
      callbacks.add(cb);
      return () => callbacks.delete(cb);
    },
    requestRender: () => ({ ok: true }),
    exportWav(options) {
      emit('render-progress', { jobId: 'wav-1', percent: 100, stage: 'complete' });
      return { ok: true, mime: 'audio/wav', bytes: new Uint8Array(44), duration: 2, sampleRate: options.sampleRate ?? 48000, channels: 2 };
    },
    dispose() { state.playing = false; },
  };
  const api = new Proxy(raw, {
    get(target, key, receiver) {
      const value: unknown = Reflect.get(target, key, receiver);
      const name = String(key);
      const record = (args: unknown[]) => {
        const entries = calls.get(name) ?? [];
        entries.push(args);
        calls.set(name, entries);
        if (failures.has(name)) throw failures.get(name);
      };
      if (typeof value === 'function') return (...args: unknown[]) => { record(args); return value.apply(target, args); };
      if (key === 'version' || key === 'ready') record([]);
      return value;
    },
  });
  return { api, raw, state, calls, failures, emit, handlers };
}
async function ok(promise: Promise<EngineResult>) {
  assert.ok(promise instanceof Promise);
  const result = await promise;
  assert.equal(result.ok, true, JSON.stringify(result));
  assert.equal(result.via, 'bridge');
  return result;
}
const flush = () => new Promise<void>((resolve) => setImmediate(resolve));

test('every FCBridge method and property is reached through the default window factory', async (t) => {
  const fake = fakeBridge();
  const original = Object.getOwnPropertyDescriptor(globalThis, 'window');
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { FCBridge: fake.api } });
  t.after(() => { if (original) Object.defineProperty(globalThis, 'window', original); else Reflect.deleteProperty(globalThis, 'window'); });
  const adapter = createEngineAdapter({ disposeBridge: true });
  await ok(adapter.version()); await ok(adapter.ready());
  await ok(adapter.openProject({ __fcproj: 1, sequence: { fps: 25 } }));
  await ok(adapter.openDemo()); await ok(adapter.getProjectSummary());
  await ok(adapter.setPlayhead(50)); assert.equal((await ok(adapter.getPlayhead())).data, 50);
  await ok(adapter.play()); assert.equal((await ok(adapter.isPlaying())).data, true);
  await ok(adapter.pause()); await ok(adapter.getSelection());
  const off = adapter.on('selection', () => {});
  await ok(adapter.selectClips(['c1'])); await ok(adapter.deselectAll()); await ok(adapter.listClips());
  await ok(adapter.updateClip('c1', { start: 25, end: 100, trackId: 'V2' }));
  await ok(adapter.addClip({ trackId: 'V1', start: 100, duration: 25, name: 'new', type: 'video' }));
  await ok(adapter.removeClips(['new1'])); await ok(adapter.undo()); await ok(adapter.redo());
  await ok(adapter.canUndo()); await ok(adapter.canRedo()); await ok(adapter.isDirty()); await ok(adapter.clearDirty());
  await ok(adapter.importAssets(['a', { assetId: 'b' }, { id: 'c' }]));
  await ok(adapter.requestRender()); await ok(adapter.exportWav()); await ok(adapter.renderFrame(75));
  assert.equal(adapter.snapshot().playhead, 3, 'renderFrame(75 帧) @25fps ⇒ snapshot.playhead 是 3 秒');
  off(); off(); await ok(adapter.closeProject()); await ok(adapter.dispose());
  const members = Object.keys(fake.raw);
  for (const member of members) assert.ok((fake.calls.get(member)?.length ?? 0) > 0, `${member} was never called`);
  assert.deepEqual([...fake.handlers.keys()].sort(), ['dirty', 'media', 'playhead', 'playstate', 'project', 'render-progress', 'selection']);
  assert.equal([...fake.handlers.values()].reduce((sum, callbacks) => sum + callbacks.size, 0), 0);
  t.diagnostic(`FCBRIDGE_MEMBERS_CALLED ${members.length}/${members.length}: ${members.join(', ')}`);
});

test('all EngineRequest variants dispatch; File is parsed and addAssets preserves at and track', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  await ok(adapter.ready());
  const project = { __fcproj: 1, sequence: { fps: 25 } };
  const requests: EngineRequest[] = [
    { type: 'openProject', file: new File([JSON.stringify(project)], 'project.json') },
    { type: 'openDemo' }, { type: 'seek', frame: 100 },
    { type: 'select', keys: ['c1'], mode: 'replace' },
    { type: 'addAssets', assetIds: ['a', 'b'], at: 125, trackId: 'V2' },
    { type: 'delete', keys: ['import-a'] }, { type: 'updateClip', key: 'c1', patch: { start: 50 } },
    { type: 'renderFrame', frame: 175 }, { type: 'exportWav', name: 'out.wav', sampleRate: 44100 },
  ];
  for (const request of requests) await ok(adapter.request(request));
  assert.deepEqual(fake.calls.get('openProject')?.[0], [project]);
  assert.deepEqual(fake.state.clips.find((clip) => clip.key === 'import-b'), { key: 'import-b', track: 'V2', start: 6, duration: 1, name: 'import-b', type: 'video' });
  assert.equal(fake.state.seconds, 7); assert.equal(fake.state.playing, false);
  const wav = await ok(adapter.request({ type: 'exportWav', name: 'out.wav' }));
  assert.equal((wav.data as { name: string }).name, 'out.wav');
  assert.equal((wav.data as { duration: number }).duration, 50);
  assert.deepEqual(await adapter.request({ type: 'delete', keys: [], ripple: true }), { ok: false, via: 'bridge', error: 'unsupported_request:ripple_delete' });
  await adapter.dispose();
});

test('a delayed File open never overwrites a later open, demo or close', async () => {
  for (const action of ['open', 'demo', 'close'] as const) {
    const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
    const old = { __fcproj: 1, name: 'old', sequence: { fps: 24 } };
    const newer = { __fcproj: 1, name: 'new', sequence: { fps: 25 } };
    const slow = new File([], 'old.json'); let release: () => void = () => {};
    slow.text = () => new Promise<string>((resolve) => { release = () => resolve(JSON.stringify(old)); });
    const pending = adapter.openProject(slow);
    if (action === 'open') await ok(adapter.openProject(newer));
    else if (action === 'demo') await ok(adapter.openDemo());
    else await ok(adapter.closeProject());
    release();
    assert.equal((await pending).error, 'request_superseded');
    assert.deepEqual((fake.calls.get('openProject') ?? []).map((args) => args[0]), action === 'open' ? [newer] : []);
    await adapter.dispose();
  }
});

test('snapshot seeds the retained engine revision on connect and route remount', async () => {
  const fake = fakeBridge(); fake.state.revision = 7;
  const first = createEngineAdapter({ bridge: fake.api });
  assert.equal(first.snapshot().revision, 7);
  await ok(first.ready()); assert.equal(first.snapshot().revision, 7); await first.dispose();
  fake.state.revision = 9;
  const second = createEngineAdapter({ bridge: fake.api });
  assert.equal(second.snapshot().revision, 9);
  await ok(second.ready()); assert.equal(second.snapshot().revision, 9); await second.dispose();
  fake.state.revision = 0;
  const empty = createEngineAdapter({ bridge: fake.api });
  assert.equal(empty.snapshot().revision, 0);
  assert.equal(empty.warnings.includes('snapshot_revision_unavailable'), false);
  await empty.dispose();
});

test('an old bridge has an unknown revision until a summary or event provides the seed', async () => {
  const fake = fakeBridge(); fake.state.revision = 7; const original = fake.raw.getProjectSummary;
  fake.raw.getProjectSummary = () => { const { revision: _revision, ...value } = original(); return value; };
  const adapter = createEngineAdapter({ bridge: fake.api });
  assert.equal(adapter.snapshot().revision, null);
  await ok(adapter.ready());
  assert.ok(adapter.warnings.includes('snapshot_revision_unavailable'));
  assert.equal(adapter.snapshot().revision, null);
  const events: EngineEventMap['project-dirty'][] = [];
  adapter.on('project-dirty', (value) => events.push(value));
  fake.emit('dirty', { dirty: true });
  assert.equal(events.at(-1)?.revision, null);
  fake.emit('dirty', { dirty: true, revision: 7 });
  assert.equal(adapter.snapshot().revision, 7);
  fake.raw.getProjectSummary = original; fake.state.revision = 9;
  assert.equal(adapter.snapshot().revision, 9); await adapter.dispose();
});

test('missing, negative and invalid bridge summary revisions never seed a local zero', async () => {
  for (const candidate of [undefined, -1, 0.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
    const fake = fakeBridge(); const original = fake.raw.getProjectSummary;
    fake.raw.getProjectSummary = () => ({ ...original(), revision: candidate });
    const adapter = createEngineAdapter({ bridge: fake.api });
    assert.equal(adapter.snapshot().revision, null);
    assert.ok(adapter.warnings.includes('snapshot_revision_unavailable'));
    fake.raw.getProjectSummary = () => ({ ...original(), revision: 7 });
    assert.equal(adapter.snapshot().revision, 7);
    await adapter.dispose();
  }
});

test('invalid runtime request returns a normalized Promise instead of throwing synchronously', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  const pending = adapter.request(null as unknown as EngineRequest);
  assert.ok(pending instanceof Promise);
  assert.deepEqual(await pending, { ok: false, via: 'bridge', error: 'invalid_request' });
  await adapter.dispose();
});

test('integer frames round-trip at fractional fps and invalid frames never mutate the bridge', async () => {
  const fps = 30000 / 1001; const fake = fakeBridge(fps); const adapter = createEngineAdapter({ bridge: fake.api });
  for (const value of [0, 1, 1798, 100000]) {
    await ok(adapter.setPlayhead(value)); assert.equal((await ok(adapter.getPlayhead())).data, value);
    assert.equal(fake.state.seconds, value);
  }
  const before = fake.calls.get('setPlayhead')?.length;
  for (const value of [-1, NaN, Infinity, -0.5]) {
    assert.equal((await adapter.setPlayhead(value)).error, 'invalid_seconds');
  }
  assert.equal(fake.calls.get('setPlayhead')?.length, before);
  const list = (await adapter.listClips()).data;
  assert.equal(list?.[0].start, 30); assert.equal(list?.[0].duration, 60); assert.equal(list?.[0].end, 90);
  await adapter.dispose();
});

test('updateClip forwards valid engine shapes unchanged and reports bridge shape rejections verbatim', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  const effects = [{ effect: 'color', enabled: true, params: { exposure: 0.2, custom: [1, 'x'] } }];
  const transition = { type: 'crossDissolve', params: { duration: 12 } };
  const result = await ok(adapter.updateClip('c1', { start: 50, end: 150, trackId: 'V2', name: 'rename', enabled: false, effects, transition }));
  const patch = fake.calls.get('updateClip')?.at(-1)?.[1];
  assert.deepEqual(patch, { start: 50, duration: 100, track: 'V2', name: 'rename', enabled: false, effects, transition });
  assert.equal((patch as Record<string, unknown>).effects, effects);
  assert.equal((patch as Record<string, unknown>).transition, transition);
  const clip = (await adapter.listClips()).data?.[0];
  assert.deepEqual(clip?.effects, effects);
  assert.deepEqual(clip?.transition, transition);
  assert.deepEqual(result.warnings, ['unknown_effect_fixture']);
  await ok(adapter.updateClip('c1', { end: 175 }));
  assert.deepEqual(fake.calls.get('updateClip')?.at(-1), ['c1', { duration: 125 }]);
  await ok(adapter.updateClip('c1', { sourceIn: 25, speed: 2 }));
  assert.deepEqual(fake.calls.get('updateClip')?.at(-1), ['c1', { sourceIn: 25, speed: 2 }]);
  const count = fake.calls.get('updateClip')?.length;
  assert.equal((await adapter.updateClip('c1', { start: 100, end: 90 })).error, 'invalid_clip_duration');
  assert.equal(fake.calls.get('updateClip')?.length, count);
  for (const field of ['effects', 'transition']) {
    const invalid = { keyed: { strength: 0.2 } };
    const rejection = { ok: false, error: 'invalid_patch', code: 'INVALID_METADATA_SHAPE', message: `${field}: invalid engine shape; 原始桥消息`, warnings: ['shape_rejected'] };
    fake.raw.updateClip = () => rejection;
    const rejected = await adapter.updateClip('c1', { [field]: invalid } as unknown as ClipPatch);
    assert.equal((fake.calls.get('updateClip')?.at(-1)?.[1] as Record<string, unknown>)[field], invalid);
    assert.equal(rejected.ok, false);
    assert.equal(rejected.error, rejection.error);
    assert.equal(rejected.code, rejection.code);
    assert.equal(rejected.message, rejection.message);
    assert.deepEqual(rejected.warnings, rejection.warnings);
    fake.failures.set('updateClip', rejection);
    assert.deepEqual(await adapter.updateClip('c1', { [field]: invalid } as unknown as ClipPatch), rejected);
    fake.failures.delete('updateClip');
  }
  await adapter.dispose();
});

test('replace/add/toggle selection uses live bridge state and handles duplicate toggle keys once', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  await ok(adapter.selectClips(['a'])); await ok(adapter.selectClips(['b'], 'add'));
  assert.deepEqual((await adapter.getSelection()).data, ['a', 'b']);
  await ok(adapter.selectClips(['a', 'a', 'c'], 'toggle')); assert.deepEqual(fake.state.keys, ['b', 'c']);
  await adapter.dispose();
});

test('all six host streams preserve payload shapes and have idempotent cleanup', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  const seen: { [E in keyof EngineEventMap]: EngineEventMap[E][] } = {
    playhead: [], playstate: [], selection: [], 'project-dirty': [], 'render-progress': [], 'media/added': [],
  };
  const offs = [
    adapter.on('playhead', (value) => seen.playhead.push(value)),
    adapter.on('playstate', (value) => seen.playstate.push(value)),
    adapter.on('selection', (value) => seen.selection.push(value)),
    adapter.on('project-dirty', (value) => seen['project-dirty'].push(value)),
    adapter.on('render-progress', (value) => seen['render-progress'].push(value)),
    adapter.on('media/added', (value) => seen['media/added'].push(value)),
  ];
  fake.emit('playhead', { frame: 51, seconds: 999 }); fake.emit('playstate', { playing: true });
  fake.emit('selection', { keys: ['a'], revision: 7 }); fake.emit('selection', { keys: ['a'], revision: 7 });
  fake.emit('dirty', { dirty: true, revision: 8 });
  fake.emit('media', { id: 'asset-id-is-not-a-clip-key', name: 'asset', duration: 1.2 });
  const withoutTime = (value: unknown) => { const { t, ...payload } = value as Record<string, unknown>; assert.equal(typeof t, 'number'); return payload; };
  assert.deepEqual(withoutTime(seen.playhead[0]), { frame: 51, seconds: 51 / 25 });
  assert.deepEqual(withoutTime(seen.playstate[0]), { playing: true });
  assert.deepEqual(seen.selection.map((value) => value.revision), [7, 8]);
  assert.deepEqual(withoutTime(seen['project-dirty'][0]), { dirty: true, revision: 8 });
  assert.deepEqual(withoutTime(seen['media/added'][0]), { key: 'asset-id-is-not-a-clip-key', name: 'asset', durationFrames: 30 });
  assert.ok(seen['media/added'][0].key);
  fake.emit('render-progress', { jobId: 'j1', percent: 40, stage: 'mixdown', phase: 'mixdown' });
  assert.deepEqual(withoutTime(seen['render-progress'][0]), { jobId: 'j1', percent: 40, stage: 'mixdown' });
  assert.equal(adapter.warnings.includes('unsupported_event:render-progress'), false);
  assert.equal(adapter.warnings.includes('media_key_null'), false);
  offs.forEach((off) => { off(); off(); }); fake.emit('selection', { keys: ['b'], revision: 9 });
  assert.equal(seen.selection.length, 2);
  assert.equal(adapter.snapshot().revision, 8);
  await ok(adapter.dispose());
  offs.forEach((off) => off()); adapter.on('playhead', () => assert.fail('disposed callback'))();
  assert.equal([...fake.handlers.values()].every((callbacks) => callbacks.size === 0), true);
});

test('media/added publishes a nonempty asset id and the duration after registration starts loading', async () => {
  for (const method of ['load', 'decode'] as const) {
    const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
    const seen: EngineEventMap['media/added'][] = [];
    adapter.on('media/added', (value) => seen.push(value));
    let finish!: () => void;
    const asset: { id: string; name: string; duration: number; decoding: Promise<void> | null }
      & Partial<Record<'load' | 'decode', () => Promise<void>>> = {
      id: `asset-${method}`, name: method, duration: 0, decoding: null,
    };
    const load = () => asset.decoding ??= new Promise<void>((resolve) => { finish = () => { asset.duration = 1.2; resolve(); }; });
    asset[method] = load;
    fake.emit('media', asset);
    assert.equal(seen.length, 0);
    load();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(seen.length, 0);
    finish();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(seen.length, 1);
    assert.equal(seen[0].key, `asset-${method}`);
    assert.ok(seen[0].key);
    assert.equal(seen[0].durationFrames, 30);
    assert.equal(adapter.warnings.includes('media_key_null'), false);
    await adapter.dispose();
  }
});

test('media publication failures are contained and stale load failures never warn after a project change or dispose', async () => {
  const missingRate = fakeBridge(); const original = missingRate.raw.getProjectSummary;
  missingRate.raw.getProjectSummary = () => { const { fps: _fps, ...summary } = original(); return summary; };
  const adapter = createEngineAdapter({ bridge: missingRate.api });
  const seen: EngineEventMap['media/added'][] = [];
  adapter.on('media/added', (value) => seen.push(value));
  const asset = { id: 'audio', duration: 0, decode() { this.duration = 1; return Promise.resolve(); } };
  missingRate.emit('media', asset); await flush();
  assert.deepEqual(seen, []);
  assert.ok(adapter.warnings.includes('media_event:frame_rate_unavailable'));
  await adapter.dispose();
  for (const supersede of ['openDemo', 'dispose'] as const) {
    const fake = fakeBridge(); const current = createEngineAdapter({ bridge: fake.api });
    const events: EngineEventMap['media/added'][] = []; current.on('media/added', (value) => events.push(value));
    let reject!: (error: Error) => void;
    const decoding = new Promise((_resolve, fail) => { reject = fail; });
    fake.emit('media', { id: 'stale', duration: 0, decoding }); await flush();
    await ok(current[supersede]()); reject(new Error('old_load_failed')); await flush();
    assert.equal(current.warnings.includes('media_load:old_load_failed'), false);
    assert.deepEqual(events, []); await current.dispose();
  }
});

test('missing revision is warned and returned only once across repeated requests', async () => {
  const fake = fakeBridge(); const original = fake.raw.getProjectSummary;
  fake.raw.getProjectSummary = () => { const { revision: _revision, ...summary } = original(); return summary; };
  const warned: string[] = [];
  const adapter = createEngineAdapter({ bridge: fake.api, onWarning: (warning) => warned.push(warning) });
  const results = [await adapter.ready(), await adapter.getProjectSummary(), await adapter.getProjectSummary(), await adapter.getSelection()];
  adapter.snapshot(); adapter.snapshot();
  assert.equal(warned.filter((warning) => warning === 'snapshot_revision_unavailable').length, 1);
  assert.equal(adapter.warnings.filter((warning) => warning === 'snapshot_revision_unavailable').length, 1);
  assert.equal(results.flatMap((result) => result.warnings ?? []).filter((warning) => warning === 'snapshot_revision_unavailable').length, 1);
  await adapter.dispose();
});

test('dispose calls optional cancelPending once and cancels an in-flight open without requiring the method', async () => {
  const fake = fakeBridge(); let cancelCount = 0; let settle!: (value: unknown) => void;
  fake.raw.openProject = () => new Promise((resolve) => { settle = resolve; });
  fake.raw.cancelPending = () => { cancelCount++; settle({ ok: false, error: 'request_superseded' }); };
  const adapter = createEngineAdapter({ bridge: fake.api });
  const opening = adapter.openProject({});
  await ok(adapter.dispose()); await ok(adapter.dispose());
  assert.equal(cancelCount, 1);
  assert.equal((await opening).error, 'request_superseded');
  const legacy = fakeBridge();
  assert.equal(typeof legacy.raw.cancelPending, 'undefined');
  await ok(createEngineAdapter({ bridge: legacy.api }).dispose());
});

test('disposing an old adapter preserves the latest open owned by another adapter', async () => {
  for (const oldHasPending of [false, true]) {
    const fake = fakeBridge(); const pending: Array<(value: unknown) => void> = []; let cancelCount = 0;
    fake.raw.openProject = () => new Promise((resolve) => pending.push(resolve));
    fake.raw.cancelPending = () => { cancelCount++; pending.at(-1)!({ ok: false, error: 'request_superseded' }); };
    const old = createEngineAdapter({ bridge: fake.api }); const current = createEngineAdapter({ bridge: fake.api });
    const oldOpening = oldHasPending ? old.openProject({}) : undefined;
    const currentOpening = current.openProject({});
    await ok(old.dispose());
    assert.equal(cancelCount, 0);
    pending.at(-1)!({ ok: true });
    await ok(currentOpening);
    if (oldOpening) { pending[0]({ ok: false, error: 'request_superseded' }); await oldOpening; }
    await ok(current.dispose());
    assert.equal(cancelCount, 1);
  }
});

test('thrown objects, Error, strings and false result envelopes normalize without losing engine_not_ready', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  for (const error of [{ ok: false, error: 'engine_not_ready' }, new Error('broken'), 'raw']) {
    fake.failures.set('play', error);
    assert.deepEqual(await adapter.play(), { ok: false, via: 'bridge', error: error instanceof Error ? error.message : typeof error === 'string' ? error : error.error });
  }
  fake.failures.delete('play'); fake.raw.removeClips = () => ({ ok: false, error: 'track_locked', warnings: ['lock'] });
  assert.deepEqual(await adapter.removeClips(['c1']), { ok: false, via: 'bridge', error: 'track_locked', warnings: ['lock'] });
  assert.equal((await adapter.openProject(new File(['{'], 'bad.json'))).ok, false);
  await adapter.dispose();
});

test('exportWav subscribes before synchronous progress, validates options and preserves bytes', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  const progress: EngineEventMap['render-progress'][] = [];
  adapter.on('render-progress', (value) => progress.push(value));
  const result = await ok(adapter.exportWav({ name: 'preview.wav', sampleRate: 44100 }));
  const data = result.data as { bytes: Uint8Array; duration: number; name: string; sampleRate: number };
  assert.deepEqual(fake.calls.get('exportWav')?.[0], [{ sampleRate: 44100 }]);
  assert.equal(data.bytes.byteLength, 44); assert.equal(data.name, 'preview.wav');
  assert.equal(data.duration, 50); assert.equal(data.sampleRate, 44100);
  assert.equal(progress[0].stage, 'complete');
  const count = fake.calls.get('exportWav')?.length;
  assert.equal((await adapter.exportWav({ sampleRate: NaN })).error, 'invalid_sample_rate');
  assert.equal(fake.calls.get('exportWav')?.length, count); await adapter.dispose();
});

test('legacy bridge progress and WAV gaps are explicit without dropping the other streams', async () => {
  const fake = fakeBridge(); delete fake.raw.exportWav; const original = fake.raw.on;
  fake.raw.on = (event, cb) => {
    if (event === 'render-progress') throw { ok: false, error: 'invalid_event' };
    return original(event, cb);
  };
  const adapter = createEngineAdapter({ bridge: fake.api }); const seen: number[] = [];
  adapter.on('playhead', (value) => seen.push(value.frame));
  await ok(adapter.setPlayhead(2));
  assert.deepEqual(seen, [50]); assert.ok(adapter.warnings.includes('unsupported_event:render-progress'));
  assert.equal((await adapter.exportWav()).error, 'unsupported_request:exportWav'); await adapter.dispose();
});

test('listClips preserves bridge fields and distinguishes null, empty and absent metadata', async () => {
  const fake = fakeBridge(); const effects = [{ effect: 'color', enabled: false, params: { strength: 0.2 } }];
  fake.state.clips[0].effects = effects; fake.state.clips[0].transition = null;
  Object.assign(fake.state.clips[0], { fps: 25, engineField: { retained: true } });
  const adapter = createEngineAdapter({ bridge: fake.api }); const listed = await adapter.listClips();
  assert.equal(listed.ok, true);
  const clip = listed.data?.[0];
  assert.equal(clip?.effects, effects); assert.equal(clip?.transition, null);
  const { track, start, duration, ...rest } = fake.state.clips[0];
  assert.deepEqual(clip, { ...rest, trackId: track, start: start * 25, duration: duration * 25, end: (start + duration) * 25 });
  fake.state.clips[0].effects = null;
  assert.equal((await adapter.listClips()).data?.[0].effects, null);
  fake.state.clips[0].effects = [];
  assert.deepEqual((await adapter.listClips()).data?.[0].effects, []);
  delete fake.state.clips[0].effects; delete fake.state.clips[0].transition;
  const absent = (await adapter.listClips()).data?.[0];
  assert.ok(absent); assert.equal('effects' in absent, false); assert.equal('transition' in absent, false);
  await adapter.dispose();
});

test('no guessed fps; old summary can use explicit host fps or opened project fps', async () => {
  const fake = fakeBridge(); const original = fake.raw.getProjectSummary;
  fake.raw.getProjectSummary = () => { const { fps: _fps, ...value } = original(); return value; };
  const adapter = createEngineAdapter({ bridge: fake.api });
  assert.equal((await adapter.setPlayhead(25)).ok, true, '秒制下 setPlayhead 不需要 fps');
  await ok(adapter.openProject({ __fcproj: 1, sequence: { fps: 25 } })); await ok(adapter.setPlayhead(25));
  await ok(adapter.openDemo()); assert.equal((await adapter.setPlayhead(25)).ok, true);
  await adapter.dispose();
  const explicit = createEngineAdapter({ bridge: fake.api, fps: () => fake.state.fps });
  await ok(explicit.setPlayhead(50)); assert.equal(fake.state.seconds, 50); await explicit.dispose();
});

test('renderFrame pauses before seeking and invalidating; snapshot is live and defensive', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  await ok(adapter.ready()); await ok(adapter.play()); await ok(adapter.renderFrame(125));
  assert.equal(fake.state.playing, false); assert.equal(fake.state.seconds, 5);
  assert.equal(fake.calls.get('requestRender')?.length, 1);
  fake.state.keys = ['c1']; const before = adapter.snapshot(); before.selectedKeys.push('mutated');
  assert.deepEqual(adapter.snapshot().selectedKeys, ['c1']);
  assert.equal(before.duration, 8, 'snapshot.duration 是秒（D44），不是帧'); await adapter.dispose();
});

test('dispose retains singleton by default, terminal disposal is opt-in and failed teardown still releases all subscriptions', async () => {
  const fake = fakeBridge(); const adapter = createEngineAdapter({ bridge: fake.api });
  await ok(adapter.ready()); await ok(adapter.play());
  const first = adapter.dispose(); assert.equal(adapter.dispose(), first); await ok(first);
  assert.equal(fake.state.playing, false); assert.equal(fake.calls.get('pause')?.length, 1);
  assert.equal(fake.calls.get('dispose'), undefined); assert.equal((await adapter.play()).error, 'adapter_disposed');
  const terminal = createEngineAdapter({ bridge: fake.api, disposeBridge: true }); await terminal.ready();
  fake.raw.dispose = () => { throw new Error('dispose_fixture'); };
  assert.equal((await terminal.dispose()).error, 'dispose_fixture');
  assert.equal([...fake.handlers.values()].every((callbacks) => callbacks.size === 0), true);
});

function domFixture(legacy = false, observe = true) {
  const resizeListeners = new Set<() => void>();
  const queries: { media: string; callbacks: Set<() => void> }[] = [];
  let resize: ResizeObserverCallback | undefined; let disconnected = false;
  const view = {
    devicePixelRatio: 1,
    ResizeObserver: observe ? class {
      constructor(callback: ResizeObserverCallback) { resize = callback; }
      observe() {} disconnect() { disconnected = true; }
    } : undefined,
    matchMedia(media: string) {
      const item = { media, callbacks: new Set<() => void>() }; queries.push(item);
      const add = (_event: string, callback: () => void) => item.callbacks.add(callback);
      const remove = (_event: string, callback: () => void) => item.callbacks.delete(callback);
      return legacy ? { addListener: (cb: () => void) => add('change', cb), removeListener: (cb: () => void) => remove('change', cb) }
        : { addEventListener: add, removeEventListener: remove };
    },
    addEventListener: (_event: string, callback: () => void) => resizeListeners.add(callback),
    removeEventListener: (_event: string, callback: () => void) => resizeListeners.delete(callback),
  };
  const host = { ownerDocument: { defaultView: view }, getContext: () => assert.fail('host must never draw a canvas') } as unknown as HTMLElement;
  return {
    host, view, queries, resizeListeners, disconnected: () => disconnected,
    size(width: number, height: number) {
      const rect: DOMRectReadOnly = { x: 0, y: 0, width, height, top: 0, left: 0, right: width, bottom: height, toJSON: () => ({ width, height }) };
      const size: ResizeObserverSize[] = [{ inlineSize: width, blockSize: height }];
      resize?.([{ target: host, contentRect: rect, borderBoxSize: size, contentBoxSize: size, devicePixelContentBoxSize: size }], {} as ResizeObserver);
    },
    dpr(value: number) { view.devicePixelRatio = value; for (const callback of [...(queries.at(-1)?.callbacks ?? [])]) callback(); },
  };
}

test('DOM adapter coalesces resize, watches and rearms DPR, disconnects without drawing', async () => {
  const fixture = domFixture(); let renders = 0;
  const dom = createEngineDomAdapter(fixture.host, async () => { renders++; return { ok: true, via: 'bridge' }; });
  await flush(); assert.equal(renders, 1);
  fixture.size(800, 450); fixture.size(800, 450); await flush(); assert.equal(renders, 2);
  fixture.dpr(2); await flush(); assert.equal(renders, 3);
  assert.equal(fixture.queries.at(-1)?.media, '(resolution: 2dppx)'); assert.equal(fixture.queries[0].callbacks.size, 0);
  fixture.size(900, 450); dom.dispose(); dom.dispose(); await flush(); assert.equal(renders, 3);
  assert.equal(fixture.disconnected(), true); assert.equal(fixture.resizeListeners.size, 0);
  assert.equal(fixture.queries.every((query) => query.callbacks.size === 0), true);
});

test('DOM legacy media queries and window resize fallback work; render errors are observable', async () => {
  const fixture = domFixture(true, false); const errors: string[] = []; let count = 0;
  const dom = createEngineDomAdapter(fixture.host, async () => { count++; return { ok: false, via: 'bridge', error: 'engine_not_ready' }; }, { onError: (error) => errors.push(error) });
  await flush(); fixture.view.devicePixelRatio = 2;
  for (const listener of fixture.resizeListeners) listener(); await flush();
  assert.equal(count, 2); assert.deepEqual(errors, ['engine_not_ready', 'engine_not_ready']); dom.dispose();
  const rejected = createEngineDomAdapter(fixture.host, () => Promise.reject(new Error('rejected_render')), { onError: (error) => errors.push(error) });
  await flush(); assert.equal(errors.at(-1), 'rejected_render'); rejected.dispose();
});

test('DOM disposal between queued microtasks cancels the engine request', async () => {
  const fixture = domFixture(); let count = 0;
  const dom = createEngineDomAdapter(fixture.host, async () => { count++; return { ok: true, via: 'bridge' }; });
  queueMicrotask(() => dom.dispose());
  await flush();
  assert.equal(count, 0);
  assert.equal(fixture.resizeListeners.size, 0);
});

test('dispose during bridge readiness never reconnects subscriptions', async () => {
  const fake = fakeBridge(); let release: () => void = () => {};
  fake.raw.ready = new Promise<void>((resolve) => { release = resolve; });
  const adapter = createEngineAdapter({ bridge: fake.api });
  const pending = adapter.ready();
  await ok(adapter.dispose()); release();
  assert.equal((await pending).error, 'adapter_disposed');
  assert.equal(fake.calls.get('on'), undefined);
});

test('addAssets reports a partial import when a target track rejects the move', async () => {
  const fake = fakeBridge();
  fake.raw.updateClip = () => ({ ok: false, error: 'track_type_mismatch' });
  const adapter = createEngineAdapter({ bridge: fake.api });
  const result = await adapter.request({ type: 'addAssets', assetIds: ['audio'], at: 25, trackId: 'A1' });
  assert.equal(result.ok, false); assert.equal(result.error, 'track_type_mismatch');
  assert.ok(result.warnings?.includes('addAssets_partial_import:imported_clips_retained'));
  assert.ok(fake.state.clips.some((clip) => clip.key === 'import-audio'));
  await adapter.dispose();
});

test('EngineAdapter owns optional DOM observer cleanup', async () => {
  const fake = fakeBridge(); const fixture = domFixture();
  const adapter = createEngineAdapter({ bridge: fake.api, host: fixture.host }); await flush();
  assert.equal(fake.calls.get('requestRender')?.length, 1); await adapter.dispose();
  assert.equal(fixture.disconnected(), true); assert.equal(fixture.resizeListeners.size, 0);
});

class FixtureAttributes {
  private attributes = new Map<string, string>();
  dataset: Record<string, string> = Object.create(null);
  private dataKey(name: string) { return name.startsWith('data-') ? name.slice(5).replace(/-([a-z])/g, (_match, letter: string) => letter.toUpperCase()) : null; }
  getAttribute(name: string): string | null {
    name = name.toLowerCase(); const key = this.dataKey(name);
    return key === null ? this.attributes.get(name) ?? null : Object.hasOwn(this.dataset, key) ? this.dataset[key] : null;
  }
  setAttribute(name: string, value: unknown) {
    name = name.toLowerCase(); const key = this.dataKey(name);
    if (key === null) this.attributes.set(name, String(value)); else this.dataset[key] = String(value);
  }
  removeAttribute(name: string) {
    name = name.toLowerCase(); const key = this.dataKey(name);
    if (key === null) this.attributes.delete(name); else delete this.dataset[key];
  }
}

function sourceBridge(initialized: boolean, viewportMode: 'canvas' | 'full' | null = 'canvas') {
  const root = viewportMode === null ? null : new FixtureAttributes();
  if (root) root.setAttribute('data-fc-viewport-mode', viewportMode);
  const document = { getElementById: (id: string) => id === 'fc-engine-root' ? root : null };
  const callbacks = new Map<string, Set<(value: unknown) => void>>();
  const bus = {
    on(name: string, cb: (value: unknown) => void) { const set = callbacks.get(name) ?? new Set(); set.add(cb); callbacks.set(name, set); return () => set.delete(cb); },
    emit(name: string, value: unknown) { for (const cb of callbacks.get(name) ?? []) cb(value); },
  };
  const sequence = { name: 'source', fps: 25, duration: 100, width: 1920, height: 1080, videoTracks: [{ id: 'V1', clips: [{ key: 'c1', start: 0, end: 25, name: 'clip', kind: 'video' }] }], audioTracks: [] };
  const timeline = { seq: initialized ? sequence : null, selection: new Set<string>(), _hist: [] as unknown[], _histIdx: -1, _commit() {}, _applyState() {}, _paintSelection() {}, deselectAll() {} };
  const engine = {
    seq: { sequence }, state: { playing: false }, playhead: 0, revision: 0, baseline: 0, bus,
    init() { timeline.seq = sequence; }, refreshPanels() {}, command() {},
    seek(value: number) { this.playhead = value; bus.emit('playhead', value); },
    togglePlay() { this.state.playing = !this.state.playing; bus.emit('playstate', this.state.playing); },
    projectChanged() { this.revision++; },
  };
  const window = { document, App: engine, Monitor: { invalidate() {} }, FCExport: {
    mixdown(sr: number, options: { onProgress(percent: number): void }) { options.onProgress(50); return { L: new Float32Array(sr), sr }; },
    wavBytes() { const bytes = new Uint8Array(44); new DataView(bytes.buffer).setUint16(22, 2, true); return bytes; },
  } } as unknown as { FCBridge: FCBridge; App: typeof engine };
  const context = vm.createContext({ window, document, App: engine, TL: timeline, TL_MIN_DUR: 1 });
  vm.runInContext(readFileSync(new URL('../../../filmcraft-preview/wwwroot/js/fc-host-bridge.js', import.meta.url), 'utf8'), context, { filename: 'fc-host-bridge.js' });
  return { api: window.FCBridge, engine, context, document, root };
}

function loaderFixture(t: TestContext) {
  const requests: string[] = [];
  const nodes: LoaderElement[] = [];
  const errorListeners = new Set<unknown>();
  const state = { failManifest: false, failScript: false, bootstrap: (): Promise<unknown> => Promise.resolve(), runClassic: (_src: string): void => {} };
  let doc: Document;
  class LoaderElement extends FixtureAttributes {
    tagName: string;
    id = ''; className = ''; textContent = ''; src = ''; hidden = false;
    parentNode: LoaderElement | null = null;
    children: LoaderElement[] = [];
    content?: LoaderElement;
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    classes = new Set<string>();
    classList = { contains: (name: string) => this.classes.has(name), add: (name: string) => this.classes.add(name), remove: (name: string) => this.classes.delete(name) };
    constructor(tagName: string) {
      super(); this.tagName = tagName; nodes.push(this);
      if (tagName === 'template') this.content = new LoaderElement('fragment');
    }
    get ownerDocument() { return doc; }
    get isConnected(): boolean { return this.tagName === 'head' || this.tagName === 'body' || (this.parentNode?.isConnected ?? false); }
    set innerHTML(html: string) {
      if (!this.content || !html.includes('fc-engine-root')) return;
      const root = new LoaderElement('div'); root.id = 'fc-engine-root';
      const app = new LoaderElement('div'); app.id = 'app'; root.appendChild(app); this.content.appendChild(root);
    }
    appendChild(node: LoaderElement): LoaderElement {
      if (node.tagName === 'fragment') { for (const child of [...node.children]) this.appendChild(child); return node; }
      node.remove(); node.parentNode = this; this.children.push(node);
      if (node.tagName === 'script') queueMicrotask(() => {
        if (!node.parentNode) return;
        if (state.failScript) node.onerror?.();
        else {
          try {
            if (node.src.endsWith('bootstrap.js')) view.__FC_ENGINE_BOOTSTRAP__ = state.bootstrap();
            else state.runClassic(node.src);
          } catch (error) {
            for (const listener of errorListeners) (listener as (event: { filename: string; message: string }) => void)({ filename: node.src, message: (error as Error).message });
          }
          node.onload?.();
        }
      });
      return node;
    }
    remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter((node) => node !== this); this.parentNode = null; }
    contains(node: LoaderElement): boolean { return node === this || this.children.some((child) => child.contains(node)); }
    matches(selector: string): boolean { return selector === `#${this.id}`; }
    querySelectorAll(selector: string): LoaderElement[] { return this.children.flatMap((node) => [...(node.matches(selector) ? [node] : []), ...node.querySelectorAll(selector)]); }
    querySelector(selector: string): LoaderElement | null { return this.querySelectorAll(selector)[0] ?? null; }
  }
  const head = new LoaderElement('head'); const body = new LoaderElement('body');
  const view = {
    FCBridge: undefined as FCBridge | undefined,
    __FC_ENGINE_BOOTSTRAP__: undefined as Promise<unknown> | undefined,
    __FC_ENGINE_LOADER__: undefined as unknown,
    setTimeout: (callback: () => void, ms: number) => globalThis.setTimeout(callback, ms),
    addEventListener: (_event: string, callback: unknown) => errorListeners.add(callback),
    removeEventListener: (_event: string, callback: unknown) => errorListeners.delete(callback),
  };
  doc = {
    head, body, defaultView: view, readyState: 'complete',
    createElement: (tagName: string) => new LoaderElement(tagName),
    getElementById: (id: string) => head.querySelector(`#${id}`) ?? body.querySelector(`#${id}`),
  } as unknown as Document;
  for (const [key, value] of Object.entries({ window: view, document: doc, MutationObserver: class { observe() {} disconnect() {} } })) {
    const original = Object.getOwnPropertyDescriptor(globalThis, key);
    Object.defineProperty(globalThis, key, { configurable: true, writable: true, value });
    t.after(() => { if (original) Object.defineProperty(globalThis, key, original); else Reflect.deleteProperty(globalThis, key); });
  }
  const manifest = {
    schema: 1, rootId: 'fc-engine-root', styles: [{ src: 'app.css', out: 'engine/app.css', dataSrc: 'app' }],
    scripts: [{ src: 'app.js', out: 'engine/app.js' }], bootstrap: { src: 'engine/bootstrap.js' },
    skeleton: { src: 'engine/skeleton.html', includesRoot: true }, portal: { src: 'engine/portal.css', bodyClass: 'fc-engine-portals' }, portalRules: [], warnings: [],
  };
  t.mock.method(globalThis, 'fetch', async (input: RequestInfo | URL) => {
    const url = String(input); requests.push(url);
    if (url.endsWith('manifest.json')) return new Response(JSON.stringify(manifest), { status: state.failManifest ? 503 : 200 });
    if (url.endsWith('skeleton.html')) return new Response('<div id="fc-engine-root"><div id="app"></div></div>');
    return new Response('/* fixture style */');
  });
  const host = () => { const element = doc.createElement('section'); doc.body.appendChild(element); return element; };
  const clean = () => {
    assert.equal(nodes.filter((node) => ['style', 'script'].includes(node.tagName) && node.parentNode !== null).length, 0);
    assert.equal(doc.getElementById('fc-engine-root'), null);
    assert.equal(body.classList.contains('fc-engine-portals'), false);
    assert.equal(view.__FC_ENGINE_LOADER__, undefined);
    assert.equal(view.__FC_ENGINE_BOOTSTRAP__, undefined);
    assert.equal(errorListeners.size, 0);
  };
  return { state, view, requests, nodes, host, clean };
}

const turn = () => new Promise<void>((resolve) => setImmediate(resolve));

test('loader clears early initialization failures and same-manifest retry has a stable error code', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host(); fixture.state.failManifest = true;
  for (let attempt = 0; attempt < 2; attempt++) {
    await assert.rejects(loadEngine(host), (error: unknown) => {
      assert.equal((error as { code: string }).code, 'engine_load_failed');
      assert.match((error as Error).message, /^engine_load_failed:/); return true;
    });
    fixture.clean();
  }
  fixture.state.failManifest = false;
  const handle = await loadEngine(host);
  assert.equal(fixture.requests.filter((url) => url.endsWith('manifest.json')).length, 3);
  assert.equal(handle.root.parentNode, host); handle.dispose();
});

test('bootstrap rejects at exactly 15s with engine_bootstrap_timeout, cleans injected resources and retries', async (t) => {
  const fixture = loaderFixture(t); t.mock.timers.enable({ apis: ['setTimeout'] });
  const host = fixture.host(); fixture.state.bootstrap = () => new Promise(() => {});
  const loading = loadEngine(host); let settled = false;
  void loading.then(() => { settled = true; }, () => { settled = true; });
  const rejected = assert.rejects(loading, (error: unknown) => {
    assert.equal((error as { code: string }).code, 'engine_bootstrap_timeout');
    assert.equal((error as Error).message, 'engine_bootstrap_timeout'); return true;
  });
  await turn(); t.mock.timers.tick(14999); await turn(); assert.equal(settled, false);
  t.mock.timers.tick(1); await rejected; fixture.clean();
  fixture.state.bootstrap = () => Promise.resolve(); const handle = await loadEngine(host);
  const fake = fakeBridge(); fixture.view.FCBridge = fake.api;
  const adapter = createEngineAdapter({ bridge: fake.api }); await ok(adapter.ready());
  t.mock.timers.tick(20000); await turn();
  assert.equal(handle.root.parentNode, host);
  assert.equal(fixture.nodes.filter((node) => node.tagName === 'style' && node.parentNode !== null).length, 2);
  assert.equal(fixture.nodes.filter((node) => node.tagName === 'script' && node.parentNode !== null).length, 1);
  await adapter.dispose(); handle.dispose();
});

test('script and bootstrap failures remove every injected script/style before a same-manifest retry', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host(); fixture.state.failScript = true;
  await assert.rejects(loadEngine(host), (error: unknown) => (error as { code: string }).code === 'engine_load_failed'); fixture.clean();
  fixture.state.failScript = false; fixture.state.bootstrap = () => Promise.reject(new Error('bootstrap_fixture'));
  await assert.rejects(loadEngine(host), /engine_load_failed: bootstrap_fixture/); fixture.clean();
  fixture.state.bootstrap = () => Promise.resolve(); const handle = await loadEngine(host);
  assert.equal(handle.root.parentNode, host); handle.dispose();
});

test('retry preserves successful classic script bindings and the engine root after bootstrap failure', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host(); const realm = vm.createContext({ document: { addEventListener() {} } });
  const source = readFileSync(new URL('../../../filmcraft-preview/wwwroot/js/ui.js', import.meta.url), 'utf8');
  let executions = 0;
  fixture.state.runClassic = () => { executions++; vm.runInContext(source, realm, { filename: 'ui.js' }); };
  fixture.state.bootstrap = () => Promise.reject(new Error('first_bootstrap_failure'));
  await assert.rejects(loadEngine(host), /engine_load_failed: first_bootstrap_failure/); fixture.clean();
  const root = fixture.nodes.find((node) => node.id === 'fc-engine-root');
  fixture.state.bootstrap = () => Promise.resolve(); const recovered = await loadEngine(host);
  assert.equal(executions, 1);
  // EngineHandle.root is typed for the real DOM; this fixture's nodes are LoaderElement instances.
  assert.equal(recovered.root as unknown, root);
  assert.equal(recovered.root.parentNode, host);
  recovered.dispose();
});

test('ready rejects at exactly 20s with engine_ready_timeout and releases the loaded runtime for retry', async (t) => {
  const fixture = loaderFixture(t); t.mock.timers.enable({ apis: ['setTimeout'] });
  const host = fixture.host(); await loadEngine(host);
  const fake = fakeBridge(); fake.raw.ready = new Promise(() => {}); fixture.view.FCBridge = fake.api;
  const adapter = createEngineAdapter({ bridge: fake.api });
  const readiness = adapter.ready(); let settled = false; void readiness.then(() => { settled = true; });
  t.mock.timers.tick(19999); await turn(); assert.equal(settled, false);
  t.mock.timers.tick(1); const result = await readiness;
  assert.equal(result.ok, false);
  assert.equal(result.error, 'engine_ready_timeout');
  assert.equal(result.code, 'engine_ready_timeout');
  fixture.clean(); await adapter.dispose();
  const retried = await loadEngine(host); assert.equal(retried.root.parentNode, host); retried.dispose();
});

test('a rejected api.ready also releases scripts/styles and leaves no rejected loader registration', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host(); await loadEngine(host);
  const fake = fakeBridge(); fake.raw.ready = Promise.reject(new Error('ready_fixture')); fixture.view.FCBridge = fake.api;
  const adapter = createEngineAdapter({ bridge: fake.api });
  assert.equal((await adapter.ready()).error, 'ready_fixture'); fixture.clean(); await adapter.dispose();
  const retried = await loadEngine(host); assert.equal(retried.root.parentNode, host); retried.dispose();
});

test('an old ready failure never removes the runtime created by a later retry', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host(); await loadEngine(host);
  const fake = fakeBridge(); fixture.view.FCBridge = fake.api;
  let rejectOld!: (error: Error) => void;
  fake.raw.ready = new Promise((_resolve, reject) => { rejectOld = reject; });
  const oldAdapter = createEngineAdapter({ bridge: fake.api }); const oldReady = oldAdapter.ready();
  fake.raw.ready = Promise.reject(new Error('newer_ready_fixture'));
  const failed = createEngineAdapter({ bridge: fake.api });
  assert.equal((await failed.ready()).error, 'newer_ready_fixture'); fixture.clean(); await failed.dispose();
  fake.raw.ready = Promise.resolve(); const retried = await loadEngine(host);
  rejectOld(new Error('old_ready_fixture'));
  assert.equal((await oldReady).error, 'old_ready_fixture');
  assert.equal(retried.root.parentNode, host);
  assert.ok(fixture.view.__FC_ENGINE_LOADER__);
  assert.equal(fixture.nodes.filter((node) => node.tagName === 'style' && node.parentNode !== null).length, 2);
  assert.equal(fixture.nodes.filter((node) => node.tagName === 'script' && node.parentNode !== null).length, 1);
  await oldAdapter.dispose(); retried.dispose();
});

test('rehosting warns in the console and both adapters while stale disposal preserves the active host', async (t) => {
  const fixture = loaderFixture(t); const warn = t.mock.method(console, 'warn', () => {});
  const firstHost = fixture.host(); const first = await loadEngine(firstHost);
  assert.equal(await loadEngine(firstHost), first); assert.equal(warn.mock.calls.length, 0);
  const fake = fakeBridge(); fixture.view.FCBridge = fake.api;
  const before = createEngineAdapter({ bridge: fake.api }); await ok(before.ready());
  const secondHost = fixture.host(); const second = await loadEngine(secondHost);
  const after = createEngineAdapter({ bridge: fake.api }); await ok(after.ready()); await ok(before.getProjectSummary());
  assert.equal(warn.mock.calls.length, 1);
  assert.equal(warn.mock.calls[0].arguments[0], 'engine_rehosted');
  assert.ok(first.warnings.includes('engine_rehosted'));
  assert.ok(second.warnings.includes('engine_rehosted'));
  assert.ok(before.warnings.includes('engine_rehosted'));
  assert.ok(after.warnings.includes('engine_rehosted'));
  assert.equal(first.dispose().removedScripts, 0); assert.equal(second.root.parentNode, secondHost);
  await before.dispose(); await after.dispose();
  const report = second.dispose(); assert.equal(report.removedScripts, 2); assert.equal(report.removedStyles, 2);
});

test('real checked-in FCBridge source preserves engine_not_ready and converts live playhead', async () => {
  const actual = sourceBridge(false); const adapter = createEngineAdapter({ bridge: actual.api, fps: 25 });
  assert.equal((await adapter.play()).error, 'engine_not_ready');
  actual.engine.init(); await ok(adapter.ready()); await ok(adapter.setPlayhead(37));
  assert.equal(actual.engine.playhead, 925, '桥内部是帧：37 秒 × 25 fps');
  assert.equal((await adapter.getPlayhead()).data, 37, '对外回读是秒');
  assert.equal(adapter.snapshot().duration, 4, 'summary.duration 是秒');
  await ok(adapter.play()); assert.equal(actual.engine.state.playing, true);
  await adapter.dispose(); assert.equal(actual.engine.state.playing, false);
});

test('real checked-in FCBridge valid engine metadata and WAV progress pass through the adapter', async () => {
  const actual = sourceBridge(true); const adapter = createEngineAdapter({ bridge: actual.api });
  await ok(adapter.ready());
  const effects = vm.runInContext('[{ effect: "color", enabled: true, params: { strength: 0.2 } }]', actual.context) as EngineClipEffect[];
  const transition = vm.runInContext('({ type: "cross", params: { duration: 12 } })', actual.context) as EngineClipTransition;
  await ok(adapter.updateClip('c1', { effects, transition }));
  const clip = (await adapter.listClips()).data?.[0];
  assert.deepEqual(clip?.effects, effects);
  assert.deepEqual(clip?.transition, transition);
  const bridgeClip = actual.api.listClips()[0];
  assert.deepEqual(clip?.effects, bridgeClip.effects);
  assert.deepEqual(clip?.transition, bridgeClip.transition);
  const progress: number[] = []; adapter.on('render-progress', (value) => progress.push(value.percent));
  const exported = await ok(adapter.exportWav({ name: 'real.wav', sampleRate: 44100 }));
  const data = exported.data as { bytes: Uint8Array; duration: number; sampleRate: number; channels: number };
  assert.equal(data.bytes.byteLength, 44); assert.equal(data.duration, 25);
  assert.equal(data.sampleRate, 44100); assert.equal(data.channels, 2);
  assert.deepEqual(progress, [0, 50, 98, 100]); await adapter.dispose();
});

test('real checked-in FCBridge rejects invalid metadata and the adapter preserves its error', async (t) => {
  const actual = sourceBridge(true); const adapter = createEngineAdapter({ bridge: actual.api });
  await ok(adapter.ready());
  for (const [expression, expectedError] of [
    ['({ effects: { color: { strength: 0.2 } } })', 'invalid_effects_shape'],
    ['({ transition: { kind: "cross" } })', 'invalid_transition_shape'],
  ]) {
    const invalid = vm.runInContext(expression, actual.context) as ClipPatch;
    const before = actual.api.listClips()[0];
    const rejected = actual.api.updateClip('c1', invalid);
    const bridgeError = rejected as { ok: boolean; error: string; code?: string; message?: string };
    assert.equal(bridgeError.ok, false);
    assert.equal(bridgeError.error, expectedError);
    const result = await adapter.updateClip('c1', invalid);
    assert.equal(result.ok, false);
    assert.equal(result.error, bridgeError.error);
    assert.equal(result.code, bridgeError.code);
    assert.equal(result.message, bridgeError.message);
    assert.deepEqual(actual.api.listClips()[0], before);
    t.diagnostic(`INVALID_METADATA ${JSON.stringify({ input: expression, bridge: rejected, adapter: result })}`);
  }
  await adapter.dispose();
});

test('real checked-in FCBridge errors and terminal disposal are normalized', async () => {
  const actual = sourceBridge(true); const adapter = createEngineAdapter({ bridge: actual.api, fps: 25, disposeBridge: true });
  await ok(adapter.ready()); assert.equal((await adapter.updateClip('missing', { start: 1 })).error, 'clip_not_found');
  await ok(adapter.dispose());
  assert.throws(() => actual.api.play(), (error: unknown) => (error as { error: string }).error === 'engine_disposed');
});

test('fixture DOM attributes retain strings, deletion, missing values and dataset reflection', (t) => {
  const fixture = loaderFixture(t); const element = fixture.host();
  assert.equal(element.getAttribute('title'), null);
  element.setAttribute('title', String(25)); assert.equal(element.getAttribute('title'), '25');
  element.setAttribute('title', ''); assert.equal(element.getAttribute('title'), '');
  element.removeAttribute('title'); assert.equal(element.getAttribute('title'), null);
  element.removeAttribute('title'); assert.equal(element.getAttribute('title'), null);
  assert.equal(element.getAttribute('data-fc-viewport-mode'), null);
  element.setAttribute('data-fc-viewport-mode', 'full'); assert.equal(element.dataset.fcViewportMode, 'full');
  element.dataset.fcViewportMode = 'canvas'; assert.equal(element.getAttribute('data-fc-viewport-mode'), 'canvas');
  element.removeAttribute('data-fc-viewport-mode'); assert.equal(element.dataset.fcViewportMode, undefined);
  assert.equal(element.getAttribute('data-fc-viewport-mode'), null);
  element.dataset.fcViewportMode = 'full'; delete element.dataset.fcViewportMode;
  assert.equal(element.getAttribute('data-fc-viewport-mode'), null);
  const attributes = new FixtureAttributes(); attributes.setAttribute('data-count', 25);
  assert.equal(attributes.getAttribute('data-count'), '25'); assert.equal(attributes.dataset.count, '25');
  assert.equal(element.ownerDocument.getElementById('missing'), null);
});

test('loader fixture exposes the real canvas and full viewport attributes', async (t) => {
  const fixture = loaderFixture(t); const host = fixture.host();
  const canvas = await loadEngine(host);
  assert.equal(canvas.root.getAttribute('data-fc-viewport-mode'), 'canvas');
  assert.equal(canvas.root.dataset.fcViewportMode, 'canvas'); canvas.dispose();
  const full = await loadEngine(host, { viewportMode: 'full' });
  assert.equal(full.root.getAttribute('data-fc-viewport-mode'), 'full');
  assert.equal(full.root.dataset.fcViewportMode, 'full'); full.dispose();
});

test('sourceBridge fixture resolves only its root and preserves the real full viewport guard', async () => {
  const canvas = sourceBridge(true);
  assert.equal(canvas.document.getElementById('fc-engine-root'), canvas.root);
  assert.equal(canvas.document.getElementById('missing'), null);
  assert.equal(canvas.root?.getAttribute('data-fc-viewport-mode'), 'canvas');
  await canvas.api.ready; canvas.api.dispose();
  const missing = sourceBridge(true, null);
  assert.equal(missing.document.getElementById('fc-engine-root'), null);
  await missing.api.ready; missing.api.dispose();
  assert.throws(() => sourceBridge(true, 'full'), /host_persistence_unavailable/);
});
