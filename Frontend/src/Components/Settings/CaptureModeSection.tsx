import { RecordingMode, Settings as SettingsType } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

const captureModes: Array<{
  id: RecordingMode;
  title: string;
  description: string;
  features: string[];
}> = [
  {
    id: 'Hybrid',
    title: 'Hybrid (Session + Buffer)',
    description:
      'Record the full game session while keeping a replay buffer for saving short highlights.',
    features: [
      'Clip without ending the session recording',
      'Full game integration features',
      'Access to AI-generated highlights',
      'Access to Bookmarks',
    ],
  },
  {
    id: 'Session',
    title: 'Session Recording',
    description:
      'Record an entire detected game session from start to finish for complete gameplay recordings.',
    features: [
      'Full session recording',
      'Full game integration features',
      'Access to AI-generated highlights',
      'Access to Bookmarks',
    ],
  },
  {
    id: 'Buffer',
    title: 'Game Replay Buffer',
    description:
      'Run a replay buffer while a game is detected and save your best moments with a hotkey.',
    features: [
      'Efficient storage usage',
      'Captures detected games',
      'No full session recording',
      'No bookmarks',
    ],
  },
];

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const appState = useAppState();
  const isRecording =
    !appState.backgroundReplayBufferActive &&
    (appState.recording != null || appState.preRecording != null);

  const selectMode = (mode: RecordingMode) => {
    if (!isRecording) updateSettings({ recordingMode: mode });
  };

  return (
    <div className="rounded-lg border border-custom bg-base-300 p-4 shadow-md">
      <div className="mb-4 flex items-center gap-2">
        <h2 className="text-xl font-semibold">Capture Mode</h2>
        {isRecording && <span className="text-xs text-warning">(locked while recording)</span>}
      </div>

      <div className="grid grid-cols-2 gap-6">
        {captureModes.map((mode) => {
          const isSelected = settings.recordingMode === mode.id;

          return (
            <button
              key={mode.id}
              type="button"
              disabled={isRecording}
              aria-pressed={isSelected}
              onClick={() => selectMode(mode.id)}
              className={`flex min-h-52 w-full flex-col rounded-lg border bg-base-200 p-4 text-left transition-all ${
                mode.id === 'Hybrid' ? 'col-span-2' : ''
              } ${isSelected ? 'border-primary' : 'border-base-400'} ${
                isRecording ? 'cursor-not-allowed opacity-60' : 'cursor-pointer hover:bg-base-300'
              }`}
            >
              <span className="mb-3 text-lg font-semibold">{mode.title}</span>
              <span className="mb-2 text-sm text-base-content">{mode.description}</span>
              <ul className="mt-auto text-xs text-base-content text-opacity-70">
                {mode.features.map((feature) => (
                  <li key={feature}>• {feature}</li>
                ))}
              </ul>
            </button>
          );
        })}
      </div>

      <label className="mt-6 flex cursor-pointer items-start gap-3 rounded-lg border border-base-400 bg-base-200 p-4">
        <input
          type="checkbox"
          name="backgroundReplayBuffer"
          checked={settings.backgroundReplayBuffer}
          onChange={(event) => updateSettings({ backgroundReplayBuffer: event.target.checked })}
          className="checkbox checkbox-primary checkbox-sm mt-0.5"
        />
        <span className="flex flex-col">
          <span className="font-semibold">Background Replay Buffer</span>
          <span className="mt-1 text-sm text-base-content text-opacity-70">
            Keep a display replay buffer ready while Segra is idle. It pauses for normal recordings
            and starts again when they finish. Only replays you save are written to disk.
          </span>
        </span>
      </label>
    </div>
  );
}
