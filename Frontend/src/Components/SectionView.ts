import { Content, ContentType } from '../Models/types';
import { SortOption } from './ContentFilters';

// Each content page in the UI has a stable sectionId used for localStorage
// keys and the scroll context. Keeping this map in one place means the
// video page, which doesn't know which section the user came from, can
// resolve a video's type back to the right filter/sort keys.
export const SECTION_ID_BY_CONTENT_TYPE: Record<ContentType, string> = {
  Session: 'sessions',
  Buffer: 'replayBuffer',
  Clip: 'clips',
  Highlight: 'highlights',
  Lowlight: 'lowlights',
};

// Read the persisted game filter for a section. Returns an empty array when
// nothing has been saved or the value is malformed.
export function readSelectedGames(sectionId: string): string[] {
  try {
    const raw = localStorage.getItem(`${sectionId}-filters`);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((g): g is string => typeof g === 'string') : [];
  } catch {
    return [];
  }
}

// Read the persisted sort option for a section. Falls back to 'newest' when
// missing or unrecognised, matching the previous ContentPage default.
export function readSortOption(sectionId: string): SortOption {
  try {
    const raw = localStorage.getItem(`${sectionId}-sort`);
    if (!raw) return 'newest';
    const parsed = JSON.parse(raw);
    if (
      parsed === 'newest' ||
      parsed === 'oldest' ||
      parsed === 'size' ||
      parsed === 'duration' ||
      parsed === 'game'
    ) {
      return parsed;
    }
    return 'newest';
  } catch {
    return 'newest';
  }
}

// "HH:MM:SS(.mmm)" → total seconds. Used by the 'duration' sort branch.
function durationToSeconds(duration: string): number {
  return duration
    .split(':')
    .reduce((acc, part) => 60 * acc + (parseInt(part, 10) || 0), 0);
}

// Apply the game filter, then sort according to `sortOption`. This mirrors the
// previous ContentPage pipeline exactly so any call site (the content list OR
// the video player's prev/next nav) iterates the videos in the same order the
// user sees them on the page.
export function filterAndSortContent(
  items: Content[],
  selectedGames: string[],
  sortOption: SortOption,
): Content[] {
  let out = items;

  if (selectedGames.length > 0) {
    out = out.filter((item) => {
      if (selectedGames.includes('Imported') && item.isImported) {
        return true;
      }
      const gameFilters = selectedGames.filter((g) => g !== 'Imported');
      return gameFilters.includes(item.game);
    });
  }

  const sorted = [...out];
  sorted.sort((a, b) => {
    switch (sortOption) {
      case 'newest':
        return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
      case 'oldest':
        return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
      case 'size':
        return (b.fileSizeKb ?? 0) - (a.fileSizeKb ?? 0);
      case 'duration':
        return durationToSeconds(b.duration) - durationToSeconds(a.duration);
      case 'game': {
        const byGame = a.game.localeCompare(b.game);
        return byGame !== 0
          ? byGame
          : new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
      }
      default:
        return 0;
    }
  });
  return sorted;
}

// Convenience wrapper that resolves both filters + sort from localStorage for
// a given section. The video page uses this on every render so prev/next
// always reflects whatever the user last had selected on the content list.
export function filterAndSortForSection(items: Content[], sectionId: string): Content[] {
  return filterAndSortContent(items, readSelectedGames(sectionId), readSortOption(sectionId));
}
