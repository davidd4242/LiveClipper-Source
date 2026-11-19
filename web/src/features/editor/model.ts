export type AiTab = 'candidates' | 'transcript' | 'chapters';

export type TrackId = 'V3' | 'V2' | 'V1' | 'A1' | 'A2' | 'A3';

export type ClipVariant =
  | 'video-a'
  | 'video-b'
  | 'video-c'
  | 'audio'
  | 'music'
  | 'title';

export interface ProjectNode {
  id: string;
  label: string;
  kind: 'folder' | 'video' | 'audio' | 'subtitle';
  depth: number;
  expanded?: boolean;
  duration?: string;
  selected?: boolean;
}

export interface MediaItem {
  id: string;
  label: string;
  kind: 'video' | 'audio' | 'title';
  duration: string;
  colors: [string, string];
  selected?: boolean;
}

export interface HighlightCandidate {
  id: string;
  start: string;
  end: string;
  startSeconds: number;
  endSeconds: number;
  title: string;
  score: number;
  rationale: string;
  quote: string;
}

export interface TranscriptCue {
  id: string;
  at: string;
  speaker: '主播' | '嘉宾';
  text: string;
  emphasis?: string;
}

export interface Chapter {
  id: string;
  at: string;
  title: string;
  heat: number;
  note: string;
}

export interface TimelineClip {
  id: string;
  label: string;
  left: number;
  width: number;
  variant: ClipVariant;
  selected?: boolean;
}

export interface TimelineTrack {
  id: TrackId;
  name: string;
  kind: 'video' | 'audio';
  clips: TimelineClip[];
}

export interface ProjectTreeData {
  project: string;
  nodes: ProjectNode[];
  media: MediaItem[];
}
