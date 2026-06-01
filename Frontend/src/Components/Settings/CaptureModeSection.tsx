import { Settings as SettingsType } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';

interface CaptureModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function CaptureModeSection({ settings, updateSettings }: CaptureModeSectionProps) {
  const appState = useAppState();
  const isRecording = appState.recording != null || appState.preRecording != null;

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Capture Mode</h2>
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
              Record the full session while keeping a replay buffer. Save short highlights with a
              hotkey without stopping the session.
            </p>
            <div className="text-xs text-base-content text-opacity-70">
              • Clip without ending the session recording
              <br />• Full game integration features
              <br />• Access to AI-generated highlights
              <br />• Access to Bookmarks
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
              Records your entire gaming session from start to finish. Ideal for content creators
              who want complete gameplay recordings.
            </p>
            <div className="text-xs text-base-content text-opacity-70">
              • Uses more storage space
              <br />
              • Full game integration features
              <br />
              • Access to AI-generated highlights
              <br />• Access to Bookmarks
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
              Continuously records in the background. Save only your best moments with a hotkey
              press.
            </p>
            <div className="text-xs text-base-content text-opacity-70">
              • Efficient storage usage
              <br />
              • No game integration
              <br />• No bookmarks
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
