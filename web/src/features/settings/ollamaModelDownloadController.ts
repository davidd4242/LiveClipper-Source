import type { OllamaModelDownloadState } from '../../api/settings';

export const OLLAMA_7B_MODEL = 'qwen2.5:7b-instruct';
// Only these verified preset tags are interchangeable here, not arbitrary 7B variants.
export function installedOllama7B(models: readonly string[] | null): string | null {
  return models?.find(model => model === OLLAMA_7B_MODEL || model === 'qwen2.5:7b-instruct-q4_K_M') ?? null;
}
interface DownloadApi {
  read: () => Promise<OllamaModelDownloadState>;
  start: (model: string) => Promise<OllamaModelDownloadState>;
  cancel: () => Promise<unknown>;
}
export function createOllamaModelDownloadController(api: DownloadApi) {
  let state = { download: null as OllamaModelDownloadState | null, busy: false, confirmOpen: false, cancelRequested: false, error: null as string | null };
  const listeners = new Set<() => void>();
  let active = true;
  let generation = 0;
  const update = (patch: Partial<typeof state>) => {
    state = { ...state, ...patch };
    for (const listener of listeners) listener();
  };
  const canStart = () => !!state.download && !state.busy && !state.error && state.download.state !== 'downloading';
  return {
    getState: () => state,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    activate() { active = true; update({ busy: false }); },
    dispose() { active = false; generation++; },
    async refresh() {
      if (!active || state.busy) return;
      const current = ++generation;
      update({ busy: true });
      try {
        const download = await api.read();
        if (active && current === generation) update({ download, error: null, cancelRequested: download.state === 'downloading' && state.cancelRequested });
      } catch (error) {
        if (active && current === generation) update({ error: error instanceof Error ? error.message : String(error) });
      } finally {
        if (active && current === generation) update({ busy: false });
      }
    },
    prepare() { if (active && canStart()) update({ confirmOpen: true }); },
    dismiss() { update({ confirmOpen: false }); },
    async confirm() {
      if (!active || !state.confirmOpen || !canStart()) return;
      const current = ++generation;
      update({ busy: true, confirmOpen: false, error: null, cancelRequested: false });
      try {
        const download = await api.start(OLLAMA_7B_MODEL);
        if (active && current === generation) update({ download });
      } catch (error) {
        if (active && current === generation) update({ error: error instanceof Error ? error.message : String(error) });
      } finally {
        if (active && current === generation) update({ busy: false });
      }
    },
    async cancel() {
      if (!active || state.busy || state.cancelRequested || state.download?.state !== 'downloading') return;
      const current = ++generation;
      update({ busy: true, error: null });
      try {
        await api.cancel();
        if (active && current === generation) update({ cancelRequested: true });
      } catch (error) {
        if (active && current === generation) update({ error: error instanceof Error ? error.message : String(error) });
      } finally {
        if (active && current === generation) update({ busy: false });
      }
    },
  };
}
