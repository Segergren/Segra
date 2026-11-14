import type { Match, MatchResult } from '../Models/types';

// Files are named after the map, in a folder per game
const MAP_IMAGES = import.meta.glob<string>('../assets/maps/*/*.webp', {
  eager: true,
  import: 'default',
});

// IGDB id to its map image folder
const MAP_IMAGE_FOLDERS: Record<number, string> = {
  7360: 'rainbow-six-siege',
};

// "Kafe Dostoyevsky" -> kafe-dostoyevsky.webp
export function getMatchMapImage(igdbId: number | undefined, match: Match): string | undefined {
  const folder = igdbId != null ? MAP_IMAGE_FOLDERS[igdbId] : undefined;
  if (!folder || !match.map) return undefined;
  const slug = match.map
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
  return MAP_IMAGES[`../assets/maps/${folder}/${slug}.webp`];
}

export const matchResultTextClass: Record<MatchResult, string> = {
  Win: 'text-success',
  Loss: 'text-error',
  Draw: 'text-gray-300',
};

// "13-7", or empty when the game has no score
export function formatMatchScore(match: Match): string {
  return match.score != null && match.opponentScore != null
    ? `${match.score}-${match.opponentScore}`
    : '';
}

export function formatMatchKda(match: Match): string {
  if (match.kills == null || match.deaths == null) return '';
  return match.assists != null
    ? `${match.kills}/${match.deaths}/${match.assists}`
    : `${match.kills}/${match.deaths}`;
}

// "2W 1L" for a list of matches, leaving out results that never happened
export function formatMatchRecord(matches: Match[]): string {
  const count = (result: MatchResult) => matches.filter((m) => m.result === result).length;
  return [
    [count('Win'), 'W'],
    [count('Loss'), 'L'],
    [count('Draw'), 'D'],
  ]
    .filter(([n]) => (n as number) > 0)
    .map(([n, letter]) => `${n}${letter}`)
    .join(' ');
}
