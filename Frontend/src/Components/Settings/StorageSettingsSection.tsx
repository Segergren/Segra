import { useState, useEffect, useMemo } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { FolderInput } from 'lucide-react';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { useModal } from '../../Context/ModalContext';
import ConfirmationModal from '../ConfirmationModal';
import Button from '../Button';
import MigrationFlow from '../MigrationFlow';
import { useAppState } from '../../Context/AppStateContext';
import { useContentMigration } from '../../Context/ContentMigrationContext';

const normalizePath = (path: string) => path.replace(/\\/g, '/').replace(/\/+$/, '');

const TYPE_FOLDERS = ['Full Sessions', 'Replay Buffers', 'Clips', 'Highlights'];

// The recording-path root a file currently lives under (the part before its content-type folder).
const deriveSourceRoot = (filePath: string) => {
  const p = normalizePath(filePath);
  const lower = p.toLowerCase();
  for (const folder of TYPE_FOLDERS) {
    const idx = lower.lastIndexOf('/' + folder.toLowerCase() + '/');
    if (idx >= 0) return p.slice(0, idx);
  }
  const lastSlash = p.lastIndexOf('/');
  return lastSlash > 0 ? p.slice(0, lastSlash) : p;
};

interface StorageSettingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function StorageSettingsSection({
  settings,
  updateSettings,
}: StorageSettingsSectionProps) {
  const appState = useAppState();
  const { isMigrating } = useContentMigration();
  const [localStorageLimit, setLocalStorageLimit] = useState<string>(String(settings.storageLimit));
  const { openModal, closeModal } = useModal();

  useEffect(() => {
    setLocalStorageLimit(String(settings.storageLimit));
  }, [settings.storageLimit]);

  // Content whose video file is stored outside the current recording path (left behind after
  // the recording path was changed). The migration button below offers to consolidate it.
  const outsideContent = useMemo(() => {
    const root = normalizePath((settings.contentFolder || '').trim());
    if (!root) return [];
    const rootPrefix = root.toLowerCase() + '/';
    return appState.content.filter((c) => {
      const filePath = normalizePath(c.filePath || '');
      if (!filePath) return false;
      return !filePath.toLowerCase().startsWith(rootPrefix);
    });
  }, [appState.content, settings.contentFolder]);

  const outsideCount = outsideContent.length;
  const outsideSizeGb = useMemo(
    () => outsideContent.reduce((sum, c) => sum + (c.fileSizeKb || 0), 0) / (1024 * 1024),
    [outsideContent],
  );
  const fromPaths = useMemo(
    () =>
      Array.from(new Set(outsideContent.map((c) => deriveSourceRoot(c.filePath || '')))).filter(
        Boolean,
      ),
    [outsideContent],
  );

  const handleBrowseClick = () => {
    sendMessageToBackend('SetVideoLocation');
  };

  const handleMigrateClick = () => {
    openModal(
      <ConfirmationModal
        title="Move videos to recording path"
        description={
          <MigrationFlow
            fromPaths={fromPaths}
            toPath={settings.contentFolder}
            count={outsideCount}
            sizeGb={outsideSizeGb}
          />
        }
        confirmText="Move Videos"
        cancelText="Cancel"
        onConfirm={() => {
          sendMessageToBackend('MigrateContent');
          closeModal();
        }}
        onCancel={closeModal}
      />,
      { size: 'xl' },
    );
  };

  const handleCacheBrowseClick = () => {
    sendMessageToBackend('SetCacheLocation');
  };

  const handleStorageLimitBlur = () => {
    const currentFolderSizeGb = appState.currentFolderSizeGb;
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

        {/* Cache Folder Path */}
        <div className="form-control">
          <label className="label pb-1">
            <span className="label-text text-base-content">Cache Path</span>
          </label>
          <div className="flex space-x-2">
            <div className="join w-full">
              <input
                type="text"
                name="cacheFolder"
                value={settings.cacheFolder}
                onChange={(e) => updateSettings({ cacheFolder: e.target.value })}
                placeholder="Enter or select folder for metadata"
                className="input input-bordered flex-1 bg-base-200 join-item"
              />
              <button
                onClick={handleCacheBrowseClick}
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

      {/* Migrate content stored outside the recording path */}
      <AnimatePresence>
        {outsideCount > 0 && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{
              opacity: 0,
              height: 0,
              transition: {
                duration: 0.2,
              },
            }}
            style={{ overflow: 'hidden' }}
          >
            <div className="mt-4 flex flex-col gap-3 rounded-lg border border-base-400 bg-base-200 p-4 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                <p className="text-base-content font-medium">
                  {outsideCount} video{outsideCount === 1 ? '' : 's'} ({outsideSizeGb.toFixed(2)}{' '}
                  GB) stored outside your recording path
                </p>
                <p className="text-sm text-base-content text-opacity-60">
                  Move them into your recording path to keep all your content in one place.
                </p>
              </div>
              <Button
                variant="primary"
                className="gap-2 shrink-0"
                onClick={handleMigrateClick}
                loading={isMigrating}
              >
                {!isMigrating && <FolderInput size={16} className="shrink-0" />}
                <span className="inline-block">
                  {isMigrating ? 'Moving...' : 'Move to Recording Path'}
                </span>
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
