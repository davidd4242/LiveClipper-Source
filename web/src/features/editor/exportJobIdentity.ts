export interface ExportJobIdentity {
  clipId: string;
  generation: number;
  title: string;
}

export type ExportJobStatus = 'generating' | 'exporting' | 'completed' | 'failed' | 'cancelled';

export interface ExportJobResult {
  ok?: boolean;
  output?: string;
  bytes?: number;
  code?: string;
  message?: string;
  exitCode?: number;
}

export interface ExportJob {
  identity: ExportJobIdentity;
  status: ExportJobStatus;
  warnings: string[];
  result: ExportJobResult | null;
  error: string | null;
}

export type ExportJobs = Record<string, ExportJob>;
export type ExportJobAction =
  | { type: 'start'; identity: ExportJobIdentity }
  | { type: 'update'; identity: ExportJobIdentity; patch: Partial<Omit<ExportJob, 'identity'>> };

export function getExportJob(jobs: ExportJobs, clipId: string | null): ExportJob | null {
  return clipId && Object.hasOwn(jobs, clipId) ? jobs[clipId] : null;
}

export function reduceExportJobs(jobs: ExportJobs, action: ExportJobAction): ExportJobs {
  const current = getExportJob(jobs, action.identity.clipId);
  if (action.type === 'start') {
    if (current && current.identity.generation >= action.identity.generation) return jobs;
    return {
      ...jobs,
      [action.identity.clipId]: {
        identity: { ...action.identity },
        status: 'generating',
        warnings: [],
        result: null,
        error: null,
      },
    };
  }
  if (!current || current.identity.generation !== action.identity.generation ||
      (current.status !== 'generating' && current.status !== 'exporting')) return jobs;
  return { ...jobs, [action.identity.clipId]: { ...current, ...action.patch } };
}
