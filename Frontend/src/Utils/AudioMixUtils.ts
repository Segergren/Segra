// Toggles a track's mute state. Full Mix (track 0) and the separate tracks are exclusive,
// so enabling one side mutes the other.
export function toggleTrackInMix(
  muted: readonly number[],
  index: number,
  trackIndices: readonly number[],
): number[] {
  if (!muted.includes(index)) return [...muted, index];
  if (index === 0) return trackIndices.filter((i) => i !== 0);
  const next = muted.filter((i) => i !== index);
  if (!next.includes(0)) next.push(0);
  return next;
}

// The default mix plays the separate tracks (Full Mix muted) at full volume
export function hasCustomAudioMix(
  muted: readonly number[] | undefined,
  volumes: Record<number, number> | undefined,
): boolean {
  if (muted && (muted.length !== 1 || muted[0] !== 0)) return true;
  return Object.values(volumes ?? {}).some((v) => v !== 1);
}
