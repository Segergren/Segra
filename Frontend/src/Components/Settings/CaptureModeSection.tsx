import { Trans, useTranslation } from 'react-i18next';
import { Settings as SettingsType, HotkeyAction } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';
import { getKeyName } from './HotkeysSection';
import { formatBufferLength } from '../../Utils/ContentFormat';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

// IGDB ids of the games whose integrations track matches
const MATCH_GAMES = [
  { igdbId: 242408, name: 'Counter-Strike 2' },
  { igdbId: 2963, name: 'Dota 2' },
  { igdbId: 115, name: 'League of Legends' },
  { igdbId: 7360, name: 'Rainbow Six Siege' },
];

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const { t } = useTranslation('recording');
  const appState = useAppState();
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.captureMode);
  const matchGameIcons = settings.airplaneMode
    ? []
    : MATCH_GAMES.map(({ igdbId, name }) => ({
        name,
        icon: appState.gameList.find((g) => g.igdbId === igdbId)?.icon,
      }));
  const showMatchGameIcons = matchGameIcons.length > 0 && matchGameIcons.every((g) => g.icon);
  const bufferLength = formatBufferLength(settings.replayBufferDuration);
  const hotkeyFor = (action: HotkeyAction, fallback: string) => {
    const hotkey = settings.keybindings.find(
      (k) => k.action === action && k.enabled && k.keys.length > 0,
    );
    return hotkey ? (
      <kbd className="kbd kbd-xs px-1.5">{hotkey.keys.map(getKeyName).join(' + ')}</kbd>
    ) : (
      fallback
    );
  };
  const saveHotkey = hotkeyFor(
    HotkeyAction.SaveReplayBuffer,
    t('captureMode.alwaysOn.hotkeyFallback'),
  );

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">{t('captureMode.title')}</h2>
        {hasPendingChanges && <span className="text-xs text-warning">{t('pendingChanges')}</span>}
      </div>
      <div className="grid grid-cols-3 gap-4">
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Session' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Session' })}
        >
          <div className="text-lg font-semibold mb-3">{t('captureMode.session.title')}</div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">{t('captureMode.session.description')}</span>{' '}
              <span className="text-primary">{t('captureMode.session.cost')}</span>
            </p>
          </div>
        </div>
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Buffer' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Buffer' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold text-center">{t('captureMode.buffer.title')}</div>
          </div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">
                {t('captureMode.buffer.description', { length: bufferLength })}
              </span>{' '}
              <span className="text-primary">{t('captureMode.buffer.cost')}</span>
            </p>
          </div>
        </div>
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Hybrid' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Hybrid' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold">{t('captureMode.hybrid.title')}</div>
          </div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">
                {t('captureMode.hybrid.description', { length: bufferLength })}
              </span>{' '}
              <span className="text-primary">{t('captureMode.hybrid.cost')}</span>
            </p>
          </div>
        </div>
      </div>
      <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400 mt-4">
        <input
          type="checkbox"
          className="checkbox checkbox-primary checkbox-sm"
          checked={settings.alwaysOnReplayBuffer}
          onChange={(e) => updateSettings({ alwaysOnReplayBuffer: e.target.checked })}
        />
        <div>
          <div className="font-semibold">{t('captureMode.alwaysOn.label')}</div>
          <div className="text-sm opacity-70 mt-0.5">
            <Trans
              ns="recording"
              i18nKey="captureMode.alwaysOn.description"
              values={{ length: bufferLength }}
              components={{ hotkey: <>{saveHotkey}</> }}
            />
          </div>
        </div>
      </label>
      <div className="mt-3 flex flex-col gap-3">
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="discardSessionsWithoutBookmarks"
            checked={settings.discardSessionsWithoutBookmarks}
            onChange={(e) => updateSettings({ discardSessionsWithoutBookmarks: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">{t('captureMode.discardSessions.label')}</div>
            <div className="text-sm opacity-70 mt-0.5">
              {t('captureMode.discardSessions.description')}
            </div>
          </div>
        </label>
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="splitSessionsIntoMatches"
            checked={settings.splitSessionsIntoMatches}
            onChange={(e) => updateSettings({ splitSessionsIntoMatches: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <span className="font-semibold">{t('captureMode.splitMatches.label')}</span>
              {showMatchGameIcons && (
                <span className="flex items-center gap-1.5">
                  {matchGameIcons.map(({ name, icon }) => (
                    <img
                      key={name}
                      src={`https://segra.tv/api/games/icon/${icon}`}
                      alt={name}
                      className="w-5 h-5 rounded object-cover"
                    />
                  ))}
                </span>
              )}
            </div>
            <div className="text-sm opacity-70 mt-0.5">
              {t('captureMode.splitMatches.description')}
              {!showMatchGameIcons &&
                ` ${t('captureMode.splitMatches.worksIn', {
                  games: t('captureMode.splitMatches.gameList', {
                    first: MATCH_GAMES.slice(0, -1)
                      .map((g) => g.name)
                      .join(t('captureMode.splitMatches.separator')),
                    last: MATCH_GAMES[MATCH_GAMES.length - 1].name,
                  }),
                })}`}
            </div>
          </div>
        </label>
      </div>
    </div>
  );
}
