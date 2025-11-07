import { useState } from 'react';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

interface StreamingSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function StreamingSettingsSection({
  settings,
  updateSettings,
}: StreamingSettingsSectionProps) {
  const [localStreamServer, setLocalStreamServer] = useState(settings.streamServer);
  const [localStreamKey, setLocalStreamKey] = useState(settings.streamKey);

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Streaming Settings</h2>

      {/* Enable Streaming Toggle */}
      <div className="form-control mb-4">
        <label className="label cursor-pointer justify-start gap-4">
          <input
            type="checkbox"
            className="toggle toggle-primary"
            checked={settings.enableStreaming}
            onChange={(e) => updateSettings({ enableStreaming: e.target.checked })}
          />
          <div>
            <span className="label-text text-base-content">Enable Streaming</span>
            <p className="text-sm text-base-content text-opacity-60">Allow streaming to Twitch</p>
          </div>
        </label>
      </div>

      {settings.enableStreaming && (
        <div className="grid grid-cols-2 gap-4">
          {/* Stream Server */}
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Stream Server</span>
            </label>
            <input
              type="text"
              className="input input-bordered bg-base-200 w-full"
              value={localStreamServer}
              onChange={(e) => setLocalStreamServer(e.target.value)}
              onBlur={() => updateSettings({ streamServer: localStreamServer })}
              placeholder="rtmp://live.twitch.tv/app/"
            />
          </div>

          {/* Stream Key */}
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Stream Key</span>
            </label>
            <input
              type="password"
              className="input input-bordered bg-base-200 w-full"
              value={localStreamKey}
              onChange={(e) => setLocalStreamKey(e.target.value)}
              onBlur={() => updateSettings({ streamKey: localStreamKey })}
              placeholder="Enter your Twitch stream key"
            />
            {localStreamKey.length === 0 && (
              <label className="label">
                <span className="label-text-alt text-base-content text-opacity-60">
                  Get your stream key from{' '}
                  <span 
                    className="link link-primary cursor-pointer"
                    onClick={(e) => {
                      e.stopPropagation();
                      sendMessageToBackend('OpenInBrowser', {
                        Url: `https://dashboard.twitch.tv/u/oiies/settings/stream#primary-stream-key`,
                      });
                    }}
                  >
                    Twitch Dashboard
                  </span>
                </span>
              </label>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
