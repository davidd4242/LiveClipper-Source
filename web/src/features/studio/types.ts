import type {
  ClipDto,
  EditorPayload,
  EditorSequenceSummary,
  LiveRoomStatus,
  ProjectRevisionInfo,
  RenderDto,
} from '../../api/types';
import type { StudioTranscriptState } from '../../api/studio';

/** 选中项目时异步加载的详情数据（/editor + transcript-state + backups + variants）。 */
export interface StudioDetail {
  editor: EditorPayload | null;
  transcriptState: StudioTranscriptState | null;
  backups: ProjectRevisionInfo[];
  variants: EditorSequenceSummary[];
  loading: boolean;
  error: string | null;
}

/** 选中项目上下文（多个面板共用）。 */
export interface StudioSelection {
  clip: ClipDto | null;
  detail: StudioDetail;
}

export type ExportAction = 'render' | 'package' | 'draft';

export type StudioRooms = LiveRoomStatus[];

export type StudioRenders = RenderDto[];
