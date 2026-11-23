import { useState, useEffect, useMemo, KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { motion, AnimatePresence } from 'framer-motion';
import { FolderInput } from 'lucide-react';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { clampInt } from '../../Utils/NumberUtils';
import { useModal } from '../../Context/ModalContext';
import ConfirmationModal from '../ConfirmationModal';
import Button from '../Button';
import MigrationFlow from '../MigrationFlow';
import { useAppState } from '../../Context/AppStateContext';
import { useContentMigration } from '../../Context/ContentMigrationContext';
import StorageUsageMeter, { sumExcludedGb } from './StorageUsageMeter';
import { AutoDeleteClipsToggle, AutoDeleteField } from './AutoDeleteSettings';

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
  const { t } = useTranslation('storage');
  const appState = useAppState();
  const { isMigrating } = useContentMigration();
  const [localStorageLimit, setLocalStorageLimit] = useState<string>(String(settings.storageLimit));
  const { openModal, closeModal } = useModal();
  const driveUsedGb = appState.recordingDriveUsedGb;
  const driveFreeGb = appState.recordingDriveFreeGb;

  useEffect(() => {
    setLocalStorageLimit(String(settings.storageLimit));
  }, [settings.storageLimit]);

  const [localContentFolder, setLocalContentFolder] = useState(settings.contentFolder);
  const [localCacheFolder, setLocalCacheFolder] = useState(settings.cacheFolder);

  useEffect(() => {
    setLocalContentFolder(settings.contentFolder);
  }, [settings.contentFolder]);

  useEffect(() => {
    setLocalCacheFolder(settings.cacheFolder);
  }, [settings.cacheFolder]);

  const blurOnEnter = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
  };

  useEffect(() => {
    sendMessageToBackend('RefreshStorageStats');
  }, []);

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
        title={t('outside.dialogTitle')}
        description={
          <MigrationFlow
            fromPaths={fromPaths}
            toPath={settings.contentFolder}
            count={outsideCount}
            sizeGb={outsideSizeGb}
          />
        }
        confirmText={t('outside.dialogConfirm')}
        cancelText={t('actions.cancel')}
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

  // A limit below the current folder size makes the next cleanup delete older recordings
  const confirmLimit = (
    limitGb: number,
    onConfirm: () => void,
    onCancel?: () => void,
    excludeClips = settings.excludeClipsFromStorageLimit,
  ) => {
    const currentFolderSizeGb =
      appState.currentFolderSizeGb -
      (excludeClips ? sumExcludedGb(appState.content, settings.contentFolder) : 0);
    if (limitGb >= currentFolderSizeGb) {
      onConfirm();
      return;
    }

    openModal(
      <ConfirmationModal
        title={t('limit.warningTitle')}
        description={t('limit.warningBody', {
          limit: limitGb,
          current: currentFolderSizeGb.toFixed(2),
        })}
        confirmText={t('limit.apply')}
        cancelText={t('actions.cancel')}
        onConfirm={() => {
          onConfirm();
          closeModal();
        }}
        onCancel={() => {
          onCancel?.();
          closeModal();
        }}
      />,
    );
  };

  const handleStorageLimitBlur = () => {
    const numericLimit = clampInt(localStorageLimit, 1, 2147483647, 1);
    setLocalStorageLimit(String(numericLimit));
    if (numericLimit === settings.storageLimit) return;

    confirmLimit(
      numericLimit,
      () => updateSettings({ storageLimit: numericLimit }),
      // Reset to the previous value
      () => setLocalStorageLimit(String(settings.storageLimit)),
    );
  };

  const handleUnlimitedChange = (unlimited: boolean) => {
    if (unlimited) {
      updateSettings({ unlimitedStorage: true });
    } else {
      confirmLimit(settings.storageLimit, () => updateSettings({ unlimitedStorage: false }));
    }
  };

  const handleExcludeClipsChange = (exclude: boolean) => {
    if (exclude || settings.unlimitedStorage) {
      updateSettings({ excludeClipsFromStorageLimit: exclude });
    } else {
      confirmLimit(
        settings.storageLimit,
        () => updateSettings({ excludeClipsFromStorageLimit: false }),
        undefined,
        false,
      );
    }
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-4">{t('title')}</h2>
      <div className="grid grid-cols-2 gap-4">
        {/* Recording Path */}
        <div className="form-control">
          <label className="label">
            <span className="label-text text-base-content">{t('paths.recording')}</span>
          </label>
          <div className="flex space-x-2">
            <div className="join w-full">
              <input
                type="text"
                name="contentFolder"
                value={localContentFolder}
                onChange={(e) => setLocalContentFolder(e.target.value)}
                onBlur={() => {
                  if (localContentFolder !== settings.contentFolder) {
                    updateSettings({ contentFolder: localContentFolder });
                  }
                }}
                onKeyDown={blurOnEnter}
                placeholder={t('paths.recordingPlaceholder')}
                className="input input-bordered flex-1 bg-base-200 join-item"
              />
              <button
                onClick={handleBrowseClick}
                className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 font-semibold join-item"
              >
                {t('paths.browse')}
              </button>
            </div>
          </div>
        </div>

        {/* Cache Folder Path */}
        <div className="form-control">
          <label className="label">
            <span className="label-text text-base-content">{t('paths.cache')}</span>
          </label>
          <div className="flex space-x-2">
            <div className="join w-full">
              <input
                type="text"
                name="cacheFolder"
                value={localCacheFolder}
                onChange={(e) => setLocalCacheFolder(e.target.value)}
                onBlur={() => {
                  if (localCacheFolder !== settings.cacheFolder) {
                    updateSettings({ cacheFolder: localCacheFolder });
                  }
                }}
                onKeyDown={blurOnEnter}
                placeholder={t('paths.cachePlaceholder')}
                className="input input-bordered flex-1 bg-base-200 join-item"
              />
              <button
                onClick={handleCacheBrowseClick}
                className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 font-semibold join-item"
              >
                {t('paths.browse')}
              </button>
            </div>
          </div>
        </div>

        {/* Storage Limit */}
        <div className="form-control">
          <label className="label block px-0">
            <span className="label-text text-base-content">{t('limit.label')}</span>
          </label>

          <div className="join w-full">
            <div className="relative flex-1">
              <input
                type="number"
                name="storageLimit"
                value={settings.unlimitedStorage ? '' : localStorageLimit}
                onChange={(e) => setLocalStorageLimit(e.target.value)}
                onBlur={handleStorageLimitBlur}
                disabled={settings.unlimitedStorage}
                placeholder={
                  settings.unlimitedStorage ? t('limit.noLimit') : t('limit.placeholder')
                }
                min="1"
                className="input input-bordered join-item bg-base-200 w-full block pr-12 outline-none focus:border-base-400 disabled:bg-base-200 disabled:border-base-400 disabled:placeholder:opacity-100 disabled:placeholder:text-base-content/50"
              />
              {!settings.unlimitedStorage && (
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  GB
                </span>
              )}
            </div>
            {/* Positioned like the input, so its left border paints over the input's edge */}
            <label className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 font-semibold join-item gap-2 relative">
              <input
                type="checkbox"
                name="unlimitedStorage"
                checked={settings.unlimitedStorage}
                onChange={(e) => handleUnlimitedChange(e.target.checked)}
                className="checkbox checkbox-primary checkbox-sm"
              />
              {t('limit.unlimited')}
            </label>
          </div>
          <div className="mt-1 px-1 text-xs opacity-70 leading-snug">
            {settings.unlimitedStorage ? t('limit.unlimitedHint') : t('limit.limitedHint')}
          </div>
        </div>

        <AutoDeleteField
          settings={settings}
          content={appState.content}
          updateSettings={updateSettings}
        />
      </div>

      <div className="mt-4">
        <StorageUsageMeter
          content={appState.content}
          contentFolder={settings.contentFolder}
          storageLimitGb={settings.storageLimit}
          unlimited={settings.unlimitedStorage}
          excludeClips={settings.excludeClipsFromStorageLimit}
          usedGb={appState.currentFolderSizeGb}
          driveUsedGb={driveUsedGb}
          driveFreeGb={driveFreeGb}
        />
      </div>

      <div className="mt-4 flex flex-col gap-3">
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="gameSubfolders"
            checked={settings.gameSubfolders}
            onChange={(e) => updateSettings({ gameSubfolders: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">{t('toggles.gameFolders.label')}</div>
            <div className="text-sm opacity-70 mt-0.5">{t('toggles.gameFolders.description')}</div>
          </div>
        </label>
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="excludeClipsFromStorageLimit"
            checked={settings.excludeClipsFromStorageLimit}
            onChange={(e) => handleExcludeClipsChange(e.target.checked)}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">{t('toggles.excludeClips.label')}</div>
            <div className="text-sm opacity-70 mt-0.5">{t('toggles.excludeClips.description')}</div>
          </div>
        </label>
        <AutoDeleteClipsToggle
          settings={settings}
          content={appState.content}
          updateSettings={updateSettings}
        />
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="removeOriginalAfterCompression"
            checked={settings.removeOriginalAfterCompression}
            onChange={(e) => updateSettings({ removeOriginalAfterCompression: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">{t('toggles.removeOriginal.label')}</div>
            <div className="text-sm opacity-70 mt-0.5">
              {t('toggles.removeOriginal.description')}
            </div>
          </div>
        </label>
      </div>

      {/* Migrate content stored outside the recording path */}
      <AnimatePresence initial={false}>
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
            <div className="mt-4 flex flex-col gap-3 rounded-lg border border-base-400 bg-base-200 p-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                <p className="text-base-content font-medium">
                  {t('outside.videos', { count: outsideCount, size: outsideSizeGb.toFixed(2) })}
                </p>
                <p className="text-sm opacity-70">{t('outside.hint')}</p>
              </div>
              <Button
                variant="primary"
                className="gap-2 shrink-0"
                onClick={handleMigrateClick}
                loading={isMigrating}
              >
                {!isMigrating && <FolderInput size={16} className="shrink-0" />}
                <span className="inline-block">
                  {isMigrating ? t('outside.moving') : t('outside.move')}
                </span>
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
