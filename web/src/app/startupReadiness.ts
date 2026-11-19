// 默认首页启动数据门槛：一次页面加载只传递一次 pending → ready | error（终态后不再变化，后台轮询无法重复触发）。
// 只回答「首页首批数据是否已 commit」；DOM/字体等绘制门槛不在此处。
import { useSyncExternalStore } from 'react';

export type HomeStartupPhase = 'pending' | 'ready' | 'error';

let phase: HomeStartupPhase = 'pending';
const listeners = new Set<() => void>();

/** 由 HomePage 在首批数据 commit 之后调用；已有终态则忽略。 */
export function reportHomeStartup(next: 'ready' | 'error'): void {
  if (phase !== 'pending') return;
  phase = next;
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

export function useHomeStartupPhase(): HomeStartupPhase {
  return useSyncExternalStore(subscribe, () => phase, () => phase);
}
