import type { Match, MatchResult } from '../Models/types';

// Files are named after the map or character, in a folder per game
const MAP_IMAGES = import.meta.glob<string>('../assets/maps/*/*.webp', {
  eager: true,
  import: 'default',
  // Kept as files so they only load when shown
  query: '?no-inline',
});
const CHARACTER_IMAGES = import.meta.glob<string>('../assets/champions/*/*.webp', {
  eager: true,
  import: 'default',
  // Kept as files so they only load when shown
  query: '?no-inline',
});

// IGDB id to its image folder. League is always on the same map, so it shows the champion.
const MAP_IMAGE_FOLDERS: Record<number, string> = {
  242408: 'counter-strike-2',
  7360: 'rainbow-six-siege',
};
const CHARACTER_IMAGE_FOLDERS: Record<number, string> = {
  115: 'league-of-legends',
};

export interface MatchImage {
  src: string;
  // Character avatars are square, map screenshots 16:9
  square: boolean;
}

// "Kafe Dostoyevsky" -> kafe-dostoyevsky
const toSlug = (name: string) =>
  name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');

export function getMatchImage(igdbId: number | undefined, match: Match): MatchImage | undefined {
  if (igdbId == null) return undefined;
  const characterFolder = CHARACTER_IMAGE_FOLDERS[igdbId];
  if (characterFolder) {
    const src =
      match.character &&
      CHARACTER_IMAGES[`../assets/champions/${characterFolder}/${toSlug(match.character)}.webp`];
    return src ? { src, square: true } : undefined;
  }
  const mapFolder = MAP_IMAGE_FOLDERS[igdbId];
  const src =
    mapFolder && match.map && MAP_IMAGES[`../assets/maps/${mapFolder}/${toSlug(match.map)}.webp`];
  return src ? { src, square: false } : undefined;
}

export const matchResultTextClass: Record<MatchResult, string> = {
  Win: 'text-success',
  Loss: 'text-error',
  Draw: 'text-gray-300',
};

export const matchResultBgClass: Record<MatchResult, string> = {
  Win: 'bg-success',
  Loss: 'bg-error',
  Draw: 'bg-gray-400',
};

// "13-7", or empty when the game has no score
function formatMatchScore(match: Match): string {
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

// The score, or K/D/A for games and modes without one
export function formatMatchStat(match: Match): string {
  return formatMatchScore(match) || formatMatchKda(match);
}

// "2W 1L" for a list of matches, leaving out results that never happened
export function getMatchRecord(matches: Match[]): { result: MatchResult; text: string }[] {
  const letters: Record<MatchResult, string> = { Win: 'W', Loss: 'L', Draw: 'D' };
  return (['Win', 'Loss', 'Draw'] as const)
    .map((result) => ({ result, count: matches.filter((m) => m.result === result).length }))
    .filter(({ count }) => count > 0)
    .map(({ result, count }) => ({ result, text: `${count}${letters[result]}` }));
}
