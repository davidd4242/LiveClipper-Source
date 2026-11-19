export interface SequencePlayheadState {
  projectId: string | null;
  activeTabId: string | null;
  times: Record<string, number>;
}

export function createSequencePlayheadState(): SequencePlayheadState {
  return { projectId: null, activeTabId: null, times: Object.create(null) };
}

export function readySequenceProjectId(
  selectedClipId: string | null,
  payloadClipId: string | null,
  loadState: string,
  hasProject: boolean,
): string | null {
  // Selection changes one render before useEditorSession clears the old payload.
  return loadState === 'ready' && hasProject && selectedClipId === payloadClipId
    ? selectedClipId
    : null;
}

export function prepareSequencePlayhead(
  state: SequencePlayheadState,
  projectId: string | null,
  tabId: string,
  duration: number,
): { resetTabs: boolean; time: number | null } {
  if (projectId === null) return { resetTabs: false, time: null };
  const resetTabs = state.projectId !== projectId;
  if (resetTabs) {
    state.projectId = projectId;
    state.activeTabId = null;
    state.times = Object.create(null);
  }
  // Wait for the primary timeline to render before seeking a newly loaded project.
  if ((resetTabs && tabId !== 'primary') || state.activeTabId === tabId) {
    return { resetTabs, time: null };
  }
  const saved = state.times[tabId];
  const time = typeof saved === 'number' && Number.isFinite(saved) ? saved : 0;
  const end = Number.isFinite(duration) ? Math.max(0, duration - 0.001) : 0;
  return { resetTabs, time: Math.min(Math.max(0, time), end) };
}

export function rememberSequencePlayhead(
  state: SequencePlayheadState,
  projectId: string | null,
  tabId: string,
  time: number,
): void {
  // Neither a stale payload nor a tab awaiting its deferred seek owns the clock.
  if (projectId !== null && state.projectId === projectId && state.activeTabId === tabId) {
    state.times[tabId] = time;
  }
}
