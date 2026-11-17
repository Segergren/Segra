// The live session card's frame per recording file, kept so it is only generated once and so the saved
// session can swap to its own thumbnail without fading in from empty
const thumbnails = new Map<string, string>();

const toKey = (filePath: string) => filePath.replace(/\\/g, '/').toLowerCase();

export const getLiveThumbnail = (filePath: string) => thumbnails.get(toKey(filePath));

export const setLiveThumbnail = (filePath: string, url: string) =>
  thumbnails.set(toKey(filePath), url);
