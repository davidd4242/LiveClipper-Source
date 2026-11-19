import { replicaSnapshotToProject } from '../../engine/replicaSnapshotToProject';

/** Read-only preflight on the same native snapshot passed to the synchronous event transaction. */
export function eventClearEffectAtBlock(projectCurrent: () => ReturnType<typeof replicaSnapshotToProject>, sourceId: string,
  selectedCount: number, clips: readonly Record<string, unknown>[]): string | null {
  if (selectedCount !== 0 || clips.length === 0) return null;
  const clearsAll = clips.every(clip => {
    const raw = clip.eventPlacement;
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return false;
    const placement = raw as Record<string, unknown>;
    return placement.version === 1 && placement.sourceId === sourceId && clip.sourceId === sourceId
      && typeof placement.eventId === 'string' && placement.eventId.trim().length > 0;
  });
  if (!clearsAll) return null;
  // effectAt lives in backend metadata, not sequence.effectAt. Project the current edit,
  // rather than using an export snapshot that exists only after save acknowledgement.
  const projection = projectCurrent();
  if (!projection.ok) return projection.warnings.map(warning => warning.message).join('; ') || 'invalid_event_snapshot';
  // Projection validates the current content; the managed empty transaction retains its time domain.
  return null;
}
