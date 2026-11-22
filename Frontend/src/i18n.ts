import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import enAccount from './locales/en/account.json';
import enApp from './locales/en/app.json';
import enAudio from './locales/en/audio.json';
import enCards from './locales/en/cards.json';
import enClips from './locales/en/clips.json';
import enCommon from './locales/en/common.json';
import enEditor from './locales/en/editor.json';
import enGames from './locales/en/games.json';
import enLibrary from './locales/en/library.json';
import enModals from './locales/en/modals.json';
import enNav from './locales/en/nav.json';
import enPreferences from './locales/en/preferences.json';
import enRecording from './locales/en/recording.json';
import enSettings from './locales/en/settings.json';
import enStorage from './locales/en/storage.json';
import zhCNAccount from './locales/zh-CN/account.json';
import zhCNApp from './locales/zh-CN/app.json';
import zhCNAudio from './locales/zh-CN/audio.json';
import zhCNCards from './locales/zh-CN/cards.json';
import zhCNClips from './locales/zh-CN/clips.json';
import zhCNCommon from './locales/zh-CN/common.json';
import zhCNEditor from './locales/zh-CN/editor.json';
import zhCNGames from './locales/zh-CN/games.json';
import zhCNLibrary from './locales/zh-CN/library.json';
import zhCNModals from './locales/zh-CN/modals.json';
import zhCNNav from './locales/zh-CN/nav.json';
import zhCNPreferences from './locales/zh-CN/preferences.json';
import zhCNRecording from './locales/zh-CN/recording.json';
import zhCNSettings from './locales/zh-CN/settings.json';
import zhCNStorage from './locales/zh-CN/storage.json';

export const SUPPORTED_LANGUAGES = ['en', 'zh-CN'] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];

/** Kept separate from the backend settings blob so switching language never needs a round trip. */
export const LANGUAGE_STORAGE_KEY = 'segra.locale';

const isLanguage = (value: string | null): value is Language =>
  value !== null && (SUPPORTED_LANGUAGES as readonly string[]).includes(value);

export const getStoredLanguage = (): Language => {
  const stored = localStorage.getItem(LANGUAGE_STORAGE_KEY);
  return isLanguage(stored) ? stored : 'en';
};

// Keep <html lang> in sync so the platform renders CJK text with the right font and line
// breaking rules.
const syncDocumentLanguage = (language: string) => {
  document.documentElement.lang = language;
};

void i18n.use(initReactI18next).init({
  resources: {
    en: {
      account: enAccount,
      app: enApp,
      audio: enAudio,
      cards: enCards,
      clips: enClips,
      common: enCommon,
      editor: enEditor,
      games: enGames,
      library: enLibrary,
      modals: enModals,
      nav: enNav,
      preferences: enPreferences,
      recording: enRecording,
      settings: enSettings,
      storage: enStorage,
    },
    'zh-CN': {
      account: zhCNAccount,
      app: zhCNApp,
      audio: zhCNAudio,
      cards: zhCNCards,
      clips: zhCNClips,
      common: zhCNCommon,
      editor: zhCNEditor,
      games: zhCNGames,
      library: zhCNLibrary,
      modals: zhCNModals,
      nav: zhCNNav,
      preferences: zhCNPreferences,
      recording: zhCNRecording,
      settings: zhCNSettings,
      storage: zhCNStorage,
    },
  },
  lng: getStoredLanguage(),
  fallbackLng: 'en',
  supportedLngs: [...SUPPORTED_LANGUAGES],
  defaultNS: 'common',
  ns: [
    'common',
    'nav',
    'settings',
    'preferences',
    'account',
    'app',
    'audio',
    'cards',
    'clips',
    'editor',
    'games',
    'library',
    'modals',
    'recording',
    'storage',
  ],
  // React already escapes interpolated values.
  interpolation: { escapeValue: false },
  // Resources are bundled, so initialise synchronously and avoid a first-paint flash of
  // untranslated keys.
  initAsync: false,
  returnNull: false,
});

i18n.on('languageChanged', syncDocumentLanguage);
syncDocumentLanguage(i18n.language);

export const setLanguage = (language: Language) => {
  localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  void i18n.changeLanguage(language);
};

export default i18n;
