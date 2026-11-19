import type { CopySaveSession, CopyStamp } from './copyBridge';

/** Use the current native projection, not the last saved payload: undo can empty a project. */
export function canExportIndependentTimeline(session: CopySaveSession | null, stamp: CopyStamp | null): boolean {
  if (!session || !stamp) return false;
  try {
    const projection = session.projectCurrentSnapshot(stamp);
    return projection.ok && (projection.project.timeline?.length ?? 0) > 0;
  } catch { return false; }
}
