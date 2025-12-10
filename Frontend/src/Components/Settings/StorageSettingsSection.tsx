import { useState, useEffect } from 'react';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { useModal } from '../../Context/ModalContext';
import ConfirmationModal from '../ConfirmationModal';

interface StorageSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function StorageSettingsSection({
  settings,
  updateSettings,
}: StorageSettingsSectionProps) {
  const [localStorageLimit, setLocalStorageLimit] = useState<string>(String(settings.storageLimit));
  const { openModal, closeModal } = useModal();

  useEffect(() => {
    setLocalStorageLimit(String(settings.storageLimit));
  }, [settings.storageLimit]);

  const handleBrowseClick = () => {
    sendMessageToBackend('SetVideoLocation');
  };

  const handleStorageLimitBlur = () => {
    const currentFolderSizeGb = settings.state.currentFolderSizeGb;
    const numericLimit = Number(localStorageLimit) || 1; // Default to 1 if empty/invalid

    // Update display if empty/invalid
    if (!localStorageLimit || isNaN(Number(localStorageLimit))) {
      setLocalStorageLimit('1');
    }

    // Check if the new limit is below the current folder size
    if (numericLimit < currentFolderSizeGb) {
      openModal(
        <ConfirmationModal
          title="Storage Limit Warning"
          description={`The storage limit you entered (${numericLimit} GB) is lower than your current folder size (${currentFolderSizeGb.toFixed(2)} GB).\n\nThis will cause older recordings to be automatically deleted to free up space.\n\nAre you sure you want to continue?`}
          confirmText="Apply Limit"
          cancelText="Cancel"
          onConfirm={() => {
            updateSettings({ storageLimit: numericLimit });
            closeModal();
          }}
          onCancel={() => {
            // Reset to the previous value
            setLocalStorageLimit(String(settings.storageLimit));
            closeModal();
          }}
        />,
      );
    } else {
      updateSettings({ storageLimit: numericLimit });
    }
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
            onChange={(e) => setLocalStorageLimit(e.target.value)}
            onBlur={handleStorageLimitBlur}
            placeholder="Set maximum storage in GB"
            min="1"
            className="input input-bordered bg-base-200 w-full block outline-none focus:border-base-400"
          />
        </div>
      </div>
    </div>
  );
}
