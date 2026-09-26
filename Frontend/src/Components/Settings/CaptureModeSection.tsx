import { Settings as SettingsType, HotkeyAction } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';
import { getKeyName } from './HotkeysSection';

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const appState = useAppState();
  const isRecording = appState.recording != null || appState.preRecording != null;
  const bufferLength = formatBufferLength(settings.replayBufferDuration);
  const hotkeyFor = (action: HotkeyAction, fallback: string) => {
    const hotkey = settings.keybindings.find(
      (k) => k.action === action && k.enabled && k.keys.length > 0,
    );
    return hotkey ? (
      <kbd className="kbd kbd-xs">{hotkey.keys.map(getKeyName).join(' + ')}</kbd>
    ) : (
      fallback
    );
  };
  const saveHotkey = hotkeyFor(HotkeyAction.SaveReplayBuffer, 'the Save Replay Buffer hotkey');
  const bookmarkHotkey = hotkeyFor(HotkeyAction.CreateBookmark, 'the Create Bookmark hotkey');

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <div className="flex items-center gap-2 mb-4">
        <h2 className="text-xl font-semibold">Capture Mode</h2>
        {isRecording && <span className="text-xs text-warning">(locked while recording)</span>}
      </div>
      <div className="mb-6">
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Hybrid' ? 'border-primary' : 'border-base-400'} ${isRecording ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer hover:bg-base-300'}`}
          onClick={() => !isRecording && updateSettings({ recordingMode: 'Hybrid' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold">Hybrid (Session + Buffer)</div>
          </div>
          <div className="text-sm text-left text-base-content">
            <p className="mb-2">
              Records your whole game session into one video. Recording starts when you launch a
              game and stops when you close it. A replay buffer runs alongside it, so you can press{' '}
              {saveHotkey} to save the last {bufferLength} as its own clip without stopping the
              session.
            </p>
            <div className="text-xs opacity-70">
              • Everything Session Recording has
              <br />• Save instant replays while you play
              <br />• Uses the most disk space
            </div>
          </div>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-6">
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Session' ? 'border-primary' : 'border-base-400'} ${isRecording ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer hover:bg-base-300'}`}
          onClick={() => !isRecording && updateSettings({ recordingMode: 'Session' })}
        >
          <div className="text-lg font-semibold mb-3">Session Recording</div>
          <div className="text-sm text-left text-base-content">
            <p className="mb-2">
              Records your whole game session into one video. Recording starts when you launch a
              game and stops when you close it. Mark moments with {bookmarkHotkey} while you play,
              and in supported games Segra bookmarks your kills and deaths automatically.
            </p>
            <div className="text-xs opacity-70">
              • Bookmarks and game integration
              <br />• AI highlights
              <br />• Large files for long sessions
            </div>
          </div>
        </div>
        <div
          className={`bg-base-200 p-4 rounded-lg flex flex-col transition-all transition-200 border ${settings.recordingMode == 'Buffer' ? 'border-primary' : 'border-base-400'} ${isRecording ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer hover:bg-base-300'}`}
          onClick={() => !isRecording && updateSettings({ recordingMode: 'Buffer' })}
        >
          <div className="flex items-center gap-2 mb-3">
            <div className="text-lg font-semibold text-center">Replay Buffer</div>
          </div>
          <div className="text-sm text-left text-base-content">
            <p className="mb-2">
              Keeps the last {bufferLength} in memory. Press {saveHotkey} to save it as a clip.
              Nothing else is written to disk.
            </p>
            <div className="text-xs opacity-70">
              • Uses almost no disk space
              <br />• No bookmarks or game integration
              <br />• No AI highlights
            </div>
          </div>
        </div>
      </div>
      <label className="flex items-center gap-3 cursor-pointer p-4 bg-base-200 rounded-lg border border-base-400 mt-6">
        <input
          type="checkbox"
          className="checkbox checkbox-primary checkbox-sm"
          checked={settings.alwaysOnReplayBuffer}
          onChange={(e) => updateSettings({ alwaysOnReplayBuffer: e.target.checked })}
        />
        <div>
          <div className="font-semibold">Always-on Replay Buffer</div>
          <div className="text-xs opacity-70 mt-0.5">
            Keeps a replay buffer of your display running when nothing else is recording, so you can
            save a replay at any time. It pauses while a game is recorded and starts again
            afterwards.
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
