import {
  createEditorJsonRequest,
  type EditorApiClientOptions,
  type EditorRequestOptions,
} from './commandSessionClient.ts';

export interface PerceptionRequestOptions extends EditorRequestOptions {
  method?: 'GET' | 'POST';
}

export interface ShotsRequest {
  /** Required by the backend; exclusive range (0, 1), with no default. */
  threshold: number;
}

export interface PerceptionFrameRate {
  numerator: number;
  denominator: number;
  value: number;
  source: 'avg_frame_rate' | 'r_frame_rate';
}

export interface ShotCut {
  /** Snapped source time in seconds. */
  t: number;
  frame: number;
  rawT: number;
  adjusted: boolean;
  adjustedBy: number;
}

export interface ShotsResponse {
  id: string;
  threshold: number;
  fps: PerceptionFrameRate;
  suggestionOnly: true;
  cuts: ShotCut[];
}

export interface LoudnessRequest {
  /** Window in seconds; inclusive range [0.1, 600], with no default. */
  window: number;
}

export interface LoudnessEnvelopePoint {
  t: number;
  end: number;
  /** Null means silence, not 0 LUFS. */
  lufs: number | null;
  maxLufs: number | null;
  samples: number;
}

export interface LoudnessPeak {
  momentaryLufs: number | null;
  samplePeakDbfs: number | null;
}

export interface LoudnessResponse {
  id: string;
  window: number;
  hasAudio: boolean;
  envelope: LoudnessEnvelopePoint[];
  peak: LoudnessPeak;
  integratedLufs: number | null;
}

export interface PerceptionClient {
  shots(clipId: string, body: ShotsRequest, options?: PerceptionRequestOptions): Promise<ShotsResponse>;
  loudness(clipId: string, body: LoudnessRequest, options?: PerceptionRequestOptions): Promise<LoudnessResponse>;
}

export function createPerceptionClient(options: EditorApiClientOptions = {}): PerceptionClient {
  const request = createEditorJsonRequest(options);

  function perceive<T>(
    clipId: string,
    resource: 'shots' | 'loudness',
    parameter: 'threshold' | 'window',
    value: number,
    requestOptions: PerceptionRequestOptions,
  ): Promise<T> {
    const path = `/api/studio/clips/${encodeURIComponent(clipId)}/perception/${resource}`;
    const method = requestOptions.method ?? 'GET';
    const init: RequestInit = {
      method,
      ...(requestOptions.signal ? { signal: requestOptions.signal } : {}),
    };
    if (method === 'POST') {
      return request<T>(path, { ...init, body: JSON.stringify({ [parameter]: value }) });
    }
    const query = new URLSearchParams({ [parameter]: String(value) });
    return request<T>(`${path}?${query.toString()}`, init);
  }

  return {
    shots(clipId, body, requestOptions = {}) {
      return perceive<ShotsResponse>(clipId, 'shots', 'threshold', body.threshold, requestOptions);
    },
    loudness(clipId, body, requestOptions = {}) {
      return perceive<LoudnessResponse>(clipId, 'loudness', 'window', body.window, requestOptions);
    },
  };
}
