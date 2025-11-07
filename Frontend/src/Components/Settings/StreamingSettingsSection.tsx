import { Settings as SettingsType } from '../../Models/types';

interface StreamingSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function StreamingSettingsSection({
  settings,
  updateSettings,
}: StreamingSettingsSectionProps) {
  return (
    <div className="space-y-4">
      <h2 className="text-2xl font-bold mb-4">Streaming Settings</h2>

      {/* Enable Streaming Toggle */}
      <div className="form-control">
        <label className="label cursor-pointer justify-start gap-4">
          <input
            type="checkbox"
            className="toggle toggle-primary"
            checked={settings.enableStreaming}
            onChange={(e) => updateSettings({ enableStreaming: e.target.checked })}
          />
          <div>
            <span className="label-text font-semibold">Enable Streaming</span>
            <p className="text-sm text-gray-400">Allow streaming to Twitch</p>
          </div>
        </label>
      </div>

      {settings.enableStreaming && (
        <>
          {/* Stream Server */}
          <div className="form-control">
            <label className="label">
              <span className="label-text font-semibold">Stream Server</span>
            </label>
            <input
              type="text"
              className="input input-bordered w-full"
              value={settings.streamServer}
              onChange={(e) => updateSettings({ streamServer: e.target.value })}
              placeholder="rtmp://live.twitch.tv/app/"
            />
            <label className="label">
              <span className="label-text-alt text-gray-400">
                Default: rtmp://live.twitch.tv/app/
              </span>
            </label>
          </div>

          {/* Stream Key */}
          <div className="form-control">
            <label className="label">
              <span className="label-text font-semibold">Stream Key</span>
            </label>
            <input
              type="password"
              className="input input-bordered w-full"
              value={settings.streamKey}
              onChange={(e) => updateSettings({ streamKey: e.target.value })}
              placeholder="Enter your Twitch stream key"
            />
            <label className="label">
              <span className="label-text-alt text-gray-400">
                Get your stream key from{' '}
                <a
                  href="https://dashboard.twitch.tv/settings/stream"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="link link-primary"
                >
                  Twitch Dashboard
                </a>
              </span>
            </label>
          </div>

          {/* Info Box */}
          <div className="alert alert-info">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              className="stroke-current shrink-0 w-6 h-6"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
                d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
              ></path>
            </svg>
            <div>
              <h3 className="font-bold">Streaming uses your video settings</h3>
              <div className="text-sm">
                The stream will use your configured resolution, frame rate, bitrate, and encoder
                settings from the Video Settings section.
              </div>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
