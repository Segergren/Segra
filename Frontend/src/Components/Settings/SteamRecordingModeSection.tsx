import { Settings as SettingsType } from '../../Models/types';

interface SteamRecordingModeSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function SteamRecordingModeSection({ settings, updateSettings }: SteamRecordingModeSectionProps) {
  const isRecording = settings.state.recording || settings.state.preRecording != null;

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-content/10">
      <div className="flex justify-between items-center mb-4">
        <h2 className="text-xl font-semibold">Game Recording</h2>
        {/* Placeholder for Learn More link */}
        <span className="text-sm text-primary cursor-pointer hover:underline">Learn More</span>
      </div>

      <p className="text-sm text-gray-400 mb-4">Select your recording mode:</p>

      <div className="space-y-2">
        {/* Recording Off */}
        <div
            className={`p-4 rounded-lg flex items-start gap-4 cursor-pointer transition-colors border ${settings.recordingMode === 'Off' ? 'bg-base-100 border-primary' : 'bg-base-200 border-transparent hover:bg-base-100'}`}
            onClick={() => !isRecording && updateSettings({ recordingMode: 'Off' })}
        >
             <div className="mt-1">
                <input
                    type="radio"
                    name="recordingMode"
                    className="radio radio-primary radio-sm"
                    checked={settings.recordingMode === 'Off'}
                    readOnly
                />
            </div>
            <div>
                <h3 className={`font-bold ${settings.recordingMode === 'Off' ? 'text-white' : 'text-gray-300'}`}>Recording Off</h3>
                <p className="text-sm text-gray-400">Segra will not record your gameplay.</p>
            </div>
        </div>

        {/* Record in Background */}
        <div
            className={`p-4 rounded-lg flex items-start gap-4 cursor-pointer transition-colors border ${settings.recordingMode === 'Background' ? 'bg-base-100 border-primary' : 'bg-base-200 border-transparent hover:bg-base-100'} ${isRecording ? 'opacity-50 cursor-not-allowed' : ''}`}
            onClick={() => !isRecording && updateSettings({ recordingMode: 'Background' })}
        >
             <div className="mt-1">
                <input
                    type="radio"
                    name="recordingMode"
                    className="radio radio-primary radio-sm"
                    checked={settings.recordingMode === 'Background'}
                    readOnly
                />
            </div>
            <div>
                <h3 className={`font-bold ${settings.recordingMode === 'Background' ? 'text-white' : 'text-gray-300'}`}>Record in Background</h3>
                <p className="text-sm text-gray-400 mb-2">
                    Segra will automatically record your gameplay when you start playing, so you don't miss those unexpected moments.
                </p>
                <p className="text-sm text-gray-400">
                    The last <span className="font-bold text-white">30 minutes</span> of video will be kept in a temporary format for you to replay or save as permanent clips.
                </p>
            </div>
        </div>

         {/* Record Manually */}
         <div
            className={`p-4 rounded-lg flex items-start gap-4 cursor-pointer transition-colors border ${settings.recordingMode === 'Manual' ? 'bg-base-100 border-primary' : 'bg-base-200 border-transparent hover:bg-base-100'} ${isRecording ? 'opacity-50 cursor-not-allowed' : ''}`}
            onClick={() => !isRecording && updateSettings({ recordingMode: 'Manual' })}
        >
             <div className="mt-1">
                <input
                    type="radio"
                    name="recordingMode"
                    className="radio radio-primary radio-sm"
                    checked={settings.recordingMode === 'Manual'}
                    readOnly
                />
            </div>
            <div>
                <h3 className={`font-bold ${settings.recordingMode === 'Manual' ? 'text-white' : 'text-gray-300'}`}>Record Manually</h3>
                <p className="text-sm text-gray-400">
                    Segra will record video only after you press <kbd className="kbd kbd-sm">Ctrl+F11</kbd> (or your configured hotkey).
                </p>
            </div>
        </div>

      </div>
    </div>
  );
}
