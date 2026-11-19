import { useCallback, useEffect, useRef, useState } from 'react';
import { createPerceptionClient, type LoudnessEnvelopePoint } from './api/perceptionClient.ts';
import { normalizeShots } from './perceptionModel.ts';

export interface PerceptionParams {
  threshold: number;
  window: number;
}

export type PerceptionStatus = 'idle' | 'loading' | 'ready' | 'error';

interface PerceptionData {
  shots: readonly number[];
  loudness: readonly LoudnessEnvelopePoint[];
}

export interface UsePerceptionResult extends PerceptionData {
  status: PerceptionStatus;
  error: Error | null;
  refresh: () => void;
}

interface CacheEntry {
  clipId: string;
  data?: PerceptionData;
}

interface CacheListener {
  clipId: string;
  key: string;
  invalidate: () => void;
}

interface PerceptionState extends PerceptionData {
  key: string | null;
  version: number;
  status: PerceptionStatus;
  error: Error | null;
}

const perceptionClient = createPerceptionClient();
const perceptionCache = new Map<string, CacheEntry>();
const cacheListeners = new Set<CacheListener>();
const emptyData: PerceptionData = { shots: [], loudness: [] };

function perceptionCacheKey(clipId: string, { threshold, window }: PerceptionParams): string {
  return JSON.stringify([clipId, threshold, window]);
}

/** Evict all entries, a clip, or one parameter pair; mounted consumers reload too. */
export function invalidatePerceptionCache(clipId?: string, params?: PerceptionParams): void {
  const exactKey = clipId !== undefined && params ? perceptionCacheKey(clipId, params) : null;
  const matches = (key: string, id: string) => (clipId === undefined || id === clipId) && (exactKey === null || key === exactKey);
  for (const [key, entry] of perceptionCache) {
    if (matches(key, entry.clipId)) perceptionCache.delete(key);
  }
  for (const listener of cacheListeners) {
    if (matches(listener.key, listener.clipId)) listener.invalidate();
  }
}

/** Analysis is source-relative and read-only. Both parameters must be chosen by the caller. */
export function usePerception(clipId: string | null | undefined, params: PerceptionParams): UsePerceptionResult {
  const { threshold, window } = params;
  const key = clipId ? perceptionCacheKey(clipId, params) : null;
  const [version, setVersion] = useState(0);
  const [state, setState] = useState<PerceptionState>({
    ...emptyData, key: null, version: 0, status: 'idle', error: null,
  });
  const activeRequest = useRef<{ key: string; controller: AbortController } | null>(null);

  useEffect(() => {
    if (!clipId || key === null) return;
    const controller = new AbortController();
    const request = { key, controller };
    activeRequest.current = request;
    let disposed = false;
    const isCurrent = () => !disposed && !controller.signal.aborted && activeRequest.current === request;
    const commit = (next: Pick<PerceptionState, 'shots' | 'loudness' | 'status' | 'error'>) => {
      if (isCurrent()) setState({ ...next, key, version });
    };
    const listener: CacheListener = {
      clipId, key,
      invalidate: () => {
        if (disposed) return;
        disposed = true;
        controller.abort();
        setVersion(value => value + 1);
      },
    };
    cacheListeners.add(listener);
    const cleanup = () => {
      disposed = true;
      cacheListeners.delete(listener);
      controller.abort();
      if (activeRequest.current === request) activeRequest.current = null;
    };

    if (!Number.isFinite(threshold) || threshold <= 0 || threshold >= 1 ||
        !Number.isFinite(window) || window < 0.1 || window > 600) {
      commit({ ...emptyData, status: 'error', error: new RangeError('Perception requires 0 < threshold < 1 and 0.1 <= window <= 600.') });
      return cleanup;
    }

    let entry = perceptionCache.get(key);
    if (entry?.data) {
      commit({ ...entry.data, status: 'ready', error: null });
      return cleanup;
    }
    if (!entry) {
      entry = { clipId };
      perceptionCache.set(key, entry);
    }
    const cacheEntry = entry;
    commit({ ...emptyData, status: 'loading', error: null });
    void Promise.all([
      perceptionClient.shots(clipId, { threshold }, { signal: controller.signal }),
      perceptionClient.loudness(clipId, { window }, { signal: controller.signal }),
    ]).then(([shots, loudness]) => {
      if (!isCurrent() || perceptionCache.get(key) !== cacheEntry) return;
      const data: PerceptionData = {
        shots: normalizeShots(shots),
        loudness: loudness.hasAudio && Array.isArray(loudness.envelope) ? loudness.envelope : [],
      };
      cacheEntry.data = data;
      commit({ ...data, status: 'ready', error: null });
    }).catch((reason: unknown) => {
      if (!isCurrent()) return;
      commit({ ...emptyData, status: 'error', error: reason instanceof Error ? reason : new Error(String(reason)) });
      controller.abort();
    });
    return cleanup;
  }, [clipId, key, threshold, window, version]);

  const refresh = useCallback(() => {
    if (!clipId || key === null || activeRequest.current?.key !== key) return;
    invalidatePerceptionCache(clipId, { threshold, window });
  }, [clipId, key, threshold, window]);

  // Do not expose the previous clip's state while its replacement effect is pending.
  const current = key !== null && state.key === key && state.version === version
    ? state
    : { ...emptyData, status: key === null ? 'idle' as const : 'loading' as const, error: null };
  return { shots: current.shots, loudness: current.loudness, status: current.status, error: current.error, refresh };
}
