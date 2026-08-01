import { RecordingMode, Settings as SettingsType } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

type CaptureModeOption = RecordingMode | 'DisplayBuffer';

const captureModes: Array<{
  id: CaptureModeOption;
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
  {
    id: 'DisplayBuffer',
    title: 'Always-On Display Buffer',
    description:
      'Start a display replay buffer when Segra launches and keep it active when games are detected.',
    features: [
      'Starts when Segra launches',
      'Captures the selected display',
      'Does not switch to game capture',
      'No bookmarks',
    ],
  },
];

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const appState = useAppState();
  const isRecording = appState.recording != null || appState.preRecording != null;
  const selectedMode: CaptureModeOption = settings.alwaysOnDisplayCapture
    ? 'DisplayBuffer'
    : settings.recordingMode;

  const selectMode = (mode: CaptureModeOption) => {
    if (isRecording) return;

    if (mode === 'DisplayBuffer') {
      updateSettings({ alwaysOnDisplayCapture: true });
      return;
    }

    updateSettings({
      alwaysOnDisplayCapture: false,
      recordingMode: mode,
    });
  };

  return (
    <div className="rounded-lg border border-custom bg-base-300 p-4 shadow-md">
      <div className="mb-4 flex items-center gap-2">
        <h2 className="text-xl font-semibold">Capture Mode</h2>
        {isRecording && <span className="text-xs text-warning">(locked while recording)</span>}
      </div>

      <div className="grid grid-cols-2 gap-6">
        {captureModes.map((mode) => {
          const isSelected = selectedMode === mode.id;

          return (
            <button
              key={mode.id}
              type="button"
              disabled={isRecording}
              aria-pressed={isSelected}
              onClick={() => selectMode(mode.id)}
              className={`flex min-h-52 w-full flex-col rounded-lg border bg-base-200 p-4 text-left transition-all ${
                isSelected ? 'border-primary' : 'border-base-400'
              } ${
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

      {selectedMode === 'DisplayBuffer' && (
        <div className="mt-4 rounded-lg border border-base-400 bg-base-200 p-4">
          <label
            className={`flex items-start gap-3 ${
              isRecording ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'
            }`}
          >
            <input
              type="checkbox"
              name="alwaysOnDisplayCaptureRecordSession"
              checked={settings.alwaysOnDisplayCaptureRecordSession}
              disabled={isRecording}
              onChange={(event) =>
                updateSettings({ alwaysOnDisplayCaptureRecordSession: event.target.checked })
              }
              className="checkbox checkbox-primary checkbox-sm mt-0.5"
            />
            <span className="flex flex-col">
              <span className="font-semibold">Also Record Full Display Sessions</span>
              <span className="mt-1 text-sm text-base-content text-opacity-70">
                Replay Buffer is always active in this mode. Enable this to also save a continuous
                full-session recording of your display.
              </span>
            </span>
          </label>
        </div>
      )}
    </div>
  );
}
