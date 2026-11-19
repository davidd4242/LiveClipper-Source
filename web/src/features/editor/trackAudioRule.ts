// Match EditorRenderService.IncludeTrackAudio: any solo selection overrides mute.
export function includeTrackAudio(
  track: number,
  mutedTracks: readonly number[] | null = null,
  soloedTracks: readonly number[] | null = null,
): boolean {
  return soloedTracks != null && soloedTracks.length > 0
    ? soloedTracks.includes(track)
    : mutedTracks?.includes(track) !== true;
}

// V1 mute is independent of the A1–A3 masks; audio solo still excludes V1.
export function includeBaseAudio(
  baseAudioMuted = false,
  mutedTracks: readonly number[] | null = null,
  soloedTracks: readonly number[] | null = null,
): boolean {
  return !baseAudioMuted && includeTrackAudio(1, mutedTracks, soloedTracks);
}

export function overlayAudible(
  track: number,
  mutedTracks: readonly number[] | null = null,
  soloedTracks: readonly number[] | null = null,
  flags: { audioDetached?: boolean; disabled?: boolean; hidden?: boolean } = {},
): boolean {
  return (track === 2 || track === 3)
    && !flags.audioDetached
    && !flags.disabled
    && !flags.hidden
    && includeTrackAudio(track, mutedTracks, soloedTracks);
}
