import { ApiError } from './client';
import type { EditorProjectIdentity } from './types';
const apiBase = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');
export interface NewEditorProject { name: string; width: number; height: number; fps: number }
async function projectsRequest<T>(init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBase}/api/studio/projects`, init);
  const body = await response.json();
  if (!response.ok) throw new ApiError(body.error || '工程操作失败', response.status);
  return body as T;
}
export const listEditorProjects = () => projectsRequest<EditorProjectIdentity[]>();
export const createEditorProject = (input: NewEditorProject) => projectsRequest<EditorProjectIdentity>({
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
});
