import { useState, useEffect } from 'react';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

interface StorageSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function StorageSettingsSection({ settings, updateSettings }: StorageSettingsSectionProps) {
  const [localStorageLimit, setLocalStorageLimit] = useState<number>(settings.storageLimit);

  useEffect(() => {
    setLocalStorageLimit(settings.storageLimit);
  }, [settings.storageLimit]);

  const handleBrowseClick = () => {
    sendMessageToBackend('SetVideoLocation');
  };
  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Storage Settings</h2>
      <div className="grid grid-cols-2 gap-4">
        {/* Recording Path */}
        <div className="form-control">
          <label className="label pb-1">
            <span className="label-text text-base-content">Recording Path</span>
          </label>
          <div className="flex space-x-2">
            <div className="join w-full">
              <input
                type="text"
                name="contentFolder"
                value={settings.contentFolder}
                onChange={(e) => updateSettings({ contentFolder: e.target.value })}
                placeholder="Enter or select folder path"
                className="input input-bordered flex-1 bg-base-200 join-item"
              />
              <button
                onClick={handleBrowseClick}
                className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 font-semibold join-item"
              >
                Browse
              </button>
            </div>
          </div>
        </div>

        {/* Storage Limit */}
        <div className="form-control">
          <label className="label block px-0 pb-1">
            <span className="label-text text-base-content">Storage Limit (GB)</span>
          </label>

          <input
            type="number"
            name="storageLimit"
            value={localStorageLimit}
            onChange={(e) => setLocalStorageLimit(Number(e.target.value))}
            onBlur={() => updateSettings({ storageLimit: localStorageLimit })}
            placeholder="Set maximum storage in GB"
            min="1"
            className="input input-bordered bg-base-200 w-full block"
          />
        </div>
      </div>
    </div>
  );
}
