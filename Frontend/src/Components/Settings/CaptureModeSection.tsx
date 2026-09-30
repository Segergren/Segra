import { Settings as SettingsType, HotkeyAction } from '../../Models/types';
import { getKeyName } from './HotkeysSection';
import {
  usePendingRecordingSettings,
  RECORDING_SETTING_GROUPS,
} from '../../Hooks/usePendingRecordingSettings';

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const hasPendingChanges = usePendingRecordingSettings(RECORDING_SETTING_GROUPS.captureMode);
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
  const bookmarkHotkey = hotkeyFor(HotkeyAction.CreateBookmark, 'the Create Bookmark hotkey');

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
          <div className="text-sm text-left text-base-content opacity-70">
            <p>
              Records each game from launch to close as one video. Press {bookmarkHotkey} to
              bookmark a moment. Supported games bookmark kills and deaths automatically.
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
          <div className="text-sm text-left text-base-content opacity-70">
            <p>
              Keeps the last {bufferLength} of gameplay in memory. Press {saveHotkey} to save it as
              a clip. Uses almost no disk space.
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
          <div className="text-sm text-left text-base-content opacity-70">
            <p>
              Records each game as one video with a replay buffer alongside. Press {saveHotkey} to
              save the last {bufferLength} as a clip. Uses the most disk space.
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
    </div>
  );
}

function formatBufferLength(seconds: number): string {
  if (seconds < 60) return `${seconds} seconds`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  const minutesText = `${minutes} minute${minutes === 1 ? '' : 's'}`;
  return rest === 0 ? minutesText : `${minutesText} ${rest} seconds`;
}
