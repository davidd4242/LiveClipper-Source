import { dbToLinear, eqAuditionPlan, type EqAuditionNode } from './eqAuditionPlan.ts';

/**
 * EQ 试听运行时（D95）。
 *
 * 设计约束，来自 D94 的硬要求：
 * 1. **没有 EQ 时一律不碰音频路由**——`createEqAudition` 不创建 AudioContext，
 *    只有第一次拿到非空 plan 才创建，所以"没加 EQ 的用户"这条路径与现在完全一致。
 * 2. 有 EQ 时把 `<video>` 接进一条 BiquadFilterChain；拿回空 plan 就断开链、直连 destination，
 *    保证"关掉 EQ 立刻回到原声"，而不是静音。
 * 3. 任何一步失败都**回落到直连**并记录原因，绝不让用户听不到声音。
 *
 * 浏览器 API 全部通过 `AudioEnv` 注入，因此这个模块能在 node 里被单测（不需要 jsdom）。
 */

export type AudioFilterLike = {
  type: string;
  frequency: { value: number };
  Q?: { value: number };
  gain?: { value: number };
  connect: (node: AudioTarget) => AudioTarget;
  disconnect: () => void;
};

export type AudioTarget = {
  connect: (node: AudioTarget) => AudioTarget;
  disconnect: () => void;
};

export type AudioEnv = {
  createContext: () => {
    state: 'suspended' | 'running' | 'closed' | string;
    destination: AudioTarget;
    currentTime?: number;
    resume: () => Promise<void>;
    close?: () => Promise<void>;
    createMediaElementSource: (element: unknown) => { connect: (node: AudioTarget) => AudioTarget; disconnect: () => void };
    createBiquadFilter: () => AudioFilterLike;
    createGain: () => { gain: { value: number }; connect: (node: AudioTarget) => AudioTarget; disconnect: () => void };
  };
  dbToLinear?: (db: number) => number;
};

export type EqAudition = {
  apply: (params: Record<string, unknown> | null | undefined) => EqAuditionState;
  dispose: () => void;
  readonly state: EqAuditionState;
};

export type EqAuditionState = {
  /** 是否真的接上了 EQ 链；false 表示当前是直通（或未初始化）。 */
  active: boolean;
  nodes: number;
  contextState: string | null;
  /** 出错时的原因，成功为 null。 */
  error: string | null;
};

const IDLE: EqAuditionState = { active: false, nodes: 0, contextState: null, error: null };

function toFilterType(node: EqAuditionNode): string {
  switch (node.kind) {
    case 'highpass': return 'highpass';
    case 'lowpass': return 'lowpass';
    case 'lowshelf': return 'lowshelf';
    case 'highshelf': return 'highshelf';
    case 'peaking': return 'peaking';
    default: return 'peaking';
  }
}

export function createEqAudition(env: AudioEnv, element: unknown): EqAudition {
  const linear = env.dbToLinear ?? dbToLinear;
  let context: ReturnType<AudioEnv['createContext']> | null = null;
  let source: { connect: (node: AudioTarget) => AudioTarget; disconnect: () => void } | null = null;
  let chain: Array<AudioFilterLike | { gain: { value: number }; connect: (n: AudioTarget) => AudioTarget; disconnect: () => void }> = [];
  let state: EqAuditionState = { ...IDLE };

  const teardown = () => {
    for (const node of chain) {
      try { node.disconnect(); } catch { /* 已断开 */ }
    }
    chain = [];
    if (source) {
      const direct = source;
      try { direct.disconnect(); } catch { /* 已断开 */ }
      try { direct.connect(context!.destination); } catch { /* 保底：直连也失败就交给浏览器默认输出 */ }
    }
  };

  // 诊断出口：让探针/支持人员能看到"当前到底有没有真的接上 EQ"，不必靠听。
  const publish = (next: EqAuditionState) => {
    state = next;
    const scope = globalThis as { __lcEqAudition?: EqAuditionState };
    scope.__lcEqAudition = next;
    return next;
  };

  const apply = (params: Record<string, unknown> | null | undefined): EqAuditionState => {
    const plan = eqAuditionPlan(params);
    if (plan.length === 0) {
      teardown();
      return publish({ active: false, nodes: 0, contextState: context?.state ?? null, error: null });
    }
    try {
      if (!context) context = env.createContext();
      if (!source) source = context.createMediaElementSource(element);
      teardown();

      const nodes: typeof chain = [];
      for (const node of plan) {
        if (node.kind === 'gain') {
          const gain = context.createGain();
          gain.gain.value = linear(node.db);
          nodes.push(gain);
          continue;
        }
        const stages = node.kind === 'highpass' || node.kind === 'lowpass' ? node.stages : 1;
        const base = node;
        for (let i = 0; i < stages; i += 1) {
          const filter = context.createBiquadFilter();
          filter.type = toFilterType(base);
          filter.frequency.value = base.freq;
          if (filter.Q) filter.Q.value = 'q' in base ? base.q : 0.707107;
          if (filter.gain && 'gain' in base) filter.gain.value = base.gain;
          nodes.push(filter);
        }
      }

      // source → node0 → node1 → … → destination
      let cursor: AudioTarget = source as AudioTarget;
      for (const node of nodes) cursor = cursor.connect(node);
      cursor.connect(context.destination);
      chain = nodes;
      const total = nodes.length;
      if (context.state === 'suspended') {
        // 浏览器要求"用户手势之后"才允许启动；播放动作就是那个手势。
        // resume 是异步的，所以成功后要**再发布一次**，否则诊断出口会一直显示 suspended。
        void context.resume()
          .then(() => publish({ active: true, nodes: total, contextState: context!.state, error: null }))
          .catch((reason: unknown) => publish({ active: true, nodes: total, contextState: context!.state, error: `eq_resume_failed: ${reason instanceof Error ? reason.message : String(reason)}` }));
      }
      return publish({ active: true, nodes: total, contextState: context.state, error: null });
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : String(reason);
      try { teardown(); } catch { /* ignore */ }
      return publish({ active: false, nodes: 0, contextState: context?.state ?? null, error: `eq_audition_failed: ${message}` });
    }
  };

  const dispose = () => {
    try { teardown(); } catch { /* ignore */ }
    if (context?.close) void context.close();
    context = null;
    source = null;
    publish({ ...IDLE });
  };

  return {
    apply,
    dispose,
    get state() { return state; },
  };
}

/**
 * 浏览器实现：把 `AudioContext` 包成 `AudioEnv`。
 * 单独放一个工厂，是为了让 `Monitor` 那边只有一行调用，也让本文件在没有 window 的环境下仍可被 import。
 */
export function browserAudioEnv(scope: { AudioContext?: new () => never } = globalThis as never): AudioEnv | null {
  const Ctor = (scope as { AudioContext?: new () => unknown }).AudioContext;
  if (!Ctor) return null;
  return {
    createContext: () => new (Ctor as new () => ReturnType<AudioEnv['createContext']>)(),
  };
}
