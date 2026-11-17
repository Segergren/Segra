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
  const saveHotkey = hotkeyFor(HotkeyAction.SaveReplayBuffer, 'the Save Replay Buffer hotkey');

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">Capture Mode</h2>
        {hasPendingChanges && (
          <span className="text-xs text-warning">(applies to next recording)</span>
        )}
      </div>
      <div className="grid grid-cols-3 gap-4">
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Session' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Session' })}
        >
          <div className="text-lg font-semibold mb-3">Session Recording</div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">
                Records each game from launch to close as one video.
              </span>{' '}
              <span className="text-primary">Uses disk space.</span>
            </p>
          </div>
        </div>
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Buffer' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Buffer' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold text-center">Replay Buffer</div>
          </div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">
                Keeps the last {bufferLength} of gameplay in memory so you can save it as a clip.
              </span>{' '}
              <span className="text-primary">Uses RAM.</span>
            </p>
          </div>
        </div>
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Hybrid' ? 'border-primary' : 'border-base-400'} cursor-pointer hover:bg-base-300`}
          onClick={() => updateSettings({ recordingMode: 'Hybrid' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold">Hybrid (Session + Buffer)</div>
          </div>
          <div className="text-sm text-left">
            <p>
              <span className="opacity-70">
                Records each game as one video and lets you save the last {bufferLength} as a clip.
              </span>{' '}
              <span className="text-primary">Uses RAM and disk space.</span>
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
          <div className="font-semibold">Always-on Replay Buffer</div>
          <div className="text-sm opacity-70 mt-0.5">
            Keeps a replay buffer of your display running when no game is recording. Press{' '}
            {saveHotkey} to save the last {bufferLength} as a clip.
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
            <div className="font-semibold">Discard Sessions Without Bookmarks</div>
            <div className="text-sm opacity-70 mt-0.5">
              Deletes a session recording when it ends unless you added a bookmark with the hotkey.
              Kills and other automatic bookmarks don&apos;t count.
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
              <span className="font-semibold">Split Sessions Into Matches</span>
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
              Saves each match as its own session and deletes the time in menus between matches.
              {!showMatchGameIcons &&
                ` Works in ${MATCH_GAMES.slice(0, -1)
                  .map((g) => g.name)
                  .join(', ')} and ${MATCH_GAMES[MATCH_GAMES.length - 1].name}.`}
            </div>
          </div>
        </label>
      </div>
    </div>
  );
}
