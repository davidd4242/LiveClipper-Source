import { useEffect, useSyncExternalStore } from 'react';

const ENTRY_INDEX = '__liveClipperNavigationIndex';
export interface AppLocation {
  pathname: string;
  search: string;
  hash: string;
  key: string;
  /** 每次 push/pop 递增；同页 query 归一（replace）保持不变，用于页面挂载身份。 */
  navId: number;
  ready: boolean;
}
type LeaveGuard = (targetHref: string) => Promise<string | null>;
type Entry = { url: URL; index: number };
type PopAttempt = { target: Entry; checking: boolean; decision: boolean | null; replaying: boolean };
const listeners = new Set<() => void>();
let guard: LeaveGuard | null = null;
let entry: Entry | null = null;
let snapshot: AppLocation;
let pending = false;
let popAttempt: PopAttempt | null = null;
let navSeq = 0;

function indexOf(state: unknown): number | null {
  const value = state && typeof state === 'object' ? (state as Record<string, unknown>)[ENTRY_INDEX] : null;
  return typeof value === 'number' && Number.isSafeInteger(value) ? value : null;
}
function entryState(index: number) {
  return { ...(window.history.state && typeof window.history.state === 'object' ? window.history.state : {}), [ENTRY_INDEX]: index };
}
function locationFor(url: URL, navId: number, ready = false): AppLocation {
  const pathname = url.pathname === '/index.html' ? '/' : url.pathname;
  return { pathname, search: url.search, hash: url.hash, key: `${pathname}${url.search}`, navId, ready };
}
function publish(next: Entry, replace = false) {
  const previous = snapshot;
  entry = next;
  if (replace) snapshot = locationFor(next.url, previous.navId, previous.ready);
  else snapshot = locationFor(next.url, ++navSeq);
  listeners.forEach(listener => listener());
}
function currentEntry(): Entry {
  if (!entry) {
    const index = indexOf(window.history.state) ?? 0;
    const url = new URL(window.location.href);
    if (url.pathname === '/index.html') url.pathname = '/';
    window.history.replaceState(entryState(index), '', url);
    entry = { url, index };
    snapshot = locationFor(url, navSeq);
  }
  return entry;
}
function getSnapshot() { currentEntry(); return snapshot; }
function subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }

export function useAppLocation(): AppLocation {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
export function registerAppLeaveGuard(next: LeaveGuard): () => void {
  guard = next;
  return () => { if (guard === next) guard = null; };
}
export function markAppRouteReady(key: string): void {
  if (getSnapshot().key !== key || snapshot.ready) return;
  snapshot = { ...snapshot, ready: true };
  listeners.forEach(listener => listener());
}
export function isAppUrl(url: URL): boolean {
  return url.origin === window.location.origin && /^(\/(?:index\.html)?|\/(?:editor(?:-next)?|studio|music|live|settings)(?:\/.*)?)$/.test(url.pathname);
}

export async function navigateApp(href: string): Promise<boolean> {
  const from = currentEntry();
  const url = new URL(href, from.url);
  if (!isAppUrl(url)) throw new Error(`应用内导航不支持此地址：${url.href}`);
  if (url.href === from.url.href) return true;
  if (pending) return false;
  pending = true;
  try {
    if (guard && await guard(url.href)) return false;
    if (entry !== from) return false;
    const next = { url, index: from.index + 1 };
    window.history.pushState(entryState(next.index), '', url);
    publish(next);
    return true;
  } finally { pending = false; }
}

/** 当前页面自身的 query 归一；保留历史 entry 身份并通知 React。 */
export function replaceAppLocation(href: string | URL): void {
  const from = currentEntry();
  const url = new URL(href, from.url);
  if (!isAppUrl(url)) throw new Error(`应用内导航不支持此地址：${url.href}`);
  if (url.href === from.url.href) return;
  window.history.replaceState(entryState(from.index), '', url);
  publish({ url, index: from.index }, true);
}

function finishPop(attempt: PopAttempt) {
  if (popAttempt !== attempt || attempt.decision === null || indexOf(window.history.state) !== currentEntry().index) return;
  if (!attempt.decision) { popAttempt = null; pending = false; return; }
  attempt.replaying = true;
  window.history.go(attempt.target.index - currentEntry().index);
}
function onPopState(event: PopStateEvent) {
  const from = currentEntry();
  const target = { url: new URL(window.location.href), index: indexOf(event.state) };
  // Entries created by this router are indexed, including the initial deep link.
  if (target.index === null) throw new Error('应用导航历史标记已丢失，请保留当前工程并刷新页面。');
  if (popAttempt?.replaying && target.index === popAttempt.target.index) {
    const attempt = popAttempt;
    popAttempt = null;
    pending = false;
    publish(attempt.target);
    return;
  }
  if (target.index !== from.index) {
    if (!popAttempt) popAttempt = { target: { ...target, index: target.index }, checking: false, decision: null, replaying: false };
    popAttempt.replaying = false;
    pending = true;
    // Restore the accepted URL before asking; cancellation never replaces a history entry.
    window.history.go(from.index - target.index);
    return;
  }
  const attempt = popAttempt;
  if (!attempt) return;
  if (!attempt.checking) {
    attempt.checking = true;
    void (async () => {
      try { attempt.decision = !(guard && await guard(attempt.target.url.href)); }
      finally { if (attempt.decision === null) attempt.decision = false; finishPop(attempt); }
    })();
  } else finishPop(attempt);
}

function onLinkClick(event: MouseEvent) {
  if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a[href]') : null;
  if (!link || link.getAttribute('href')?.startsWith('#') || link.hasAttribute('download') ||
    (link.target && link.target !== '_self') || link.relList.contains('external')) return;
  const url = new URL(link.href);
  if (!isAppUrl(url)) return;
  event.preventDefault();
  event.stopPropagation();
  void navigateApp(url.href);
}
export function useAppNavigationEvents(): void {
  useEffect(() => {
    currentEntry();
    window.addEventListener('popstate', onPopState);
    document.addEventListener('click', onLinkClick, true);
    return () => {
      window.removeEventListener('popstate', onPopState);
      document.removeEventListener('click', onLinkClick, true);
    };
  }, []);
}

/** Mount below Suspense: this signals a committed page, not completion of its data requests. */
export function AppRouteCommitted({ routeKey, editor }: { routeKey: string; editor: boolean }) {
  useEffect(() => { if (!editor) markAppRouteReady(routeKey); }, [routeKey, editor]);
  return null;
}
