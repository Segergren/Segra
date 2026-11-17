import type { GameListEntry, GameSetting } from '../Models/types';

// The icon URL for a game, from the catalog or the user's own game settings
export function gameIconSource(
  game: string,
  igdbId: number | undefined,
  gameList: GameListEntry[],
  gameSettings: GameSetting[],
  airplaneMode: boolean,
): string | null {
  const listed =
    gameList.find((g) => igdbId != null && g.igdbId === igdbId) ??
    gameList.find((g) => g.name === game);
  const setting = gameSettings.find((g) => g.name === game);
  const iconId = listed?.icon || setting?.icon;
  if (iconId && !airplaneMode) return `https://segra.tv/api/games/icon/${iconId}`;
  if (setting?.customIcon) return `data:image/png;base64,${setting.customIcon}`;
  return null;
}
