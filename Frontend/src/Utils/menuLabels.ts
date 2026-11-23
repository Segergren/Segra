import type { MenuItemId } from '../Models/types';

// MenuItemId values double as the persisted setting, the content-type lookup key and a scroll
// anchor, so they stay untranslated; only the rendered label is mapped here.
export const MENU_LABEL_KEYS: Record<MenuItemId, string> = {
  'Full Sessions': 'fullSessions',
  'Replay Buffer': 'replayBuffer',
  Clips: 'clips',
  Highlights: 'highlights',
  Settings: 'settings',
};

export const menuLabelKey = (id: MenuItemId) => `nav:${MENU_LABEL_KEYS[id]}`;
