// Lets other pages open Settings at a section. The request expires instead of being cleared on
// read, so a remount right after navigating (StrictMode) still lands on the same section.
const REQUEST_LIFETIME_MS = 1500;
let request: { id: string; at: number } | null = null;

export function requestSettingsSection(id: string) {
  request = { id, at: Date.now() };
}

export function getRequestedSettingsSection(): string | null {
  if (!request || Date.now() - request.at > REQUEST_LIFETIME_MS) {
    request = null;
    return null;
  }
  return request.id;
}
