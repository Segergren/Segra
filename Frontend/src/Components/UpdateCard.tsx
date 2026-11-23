import { useTranslation } from 'react-i18next';
import { useUpdate } from '../Context/UpdateContext';
import { Download, Check, TriangleAlert, X } from 'lucide-react';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { GithubIcon } from './icons/BrandIcons';
import Button from './Button';

export default function UpdateCard() {
  const { t } = useTranslation('cards');
  const { updateInfo, openReleaseNotesModal, clearUpdateInfo } = useUpdate();

  if (!updateInfo) return null;

  const getStatusIcon = () => {
    switch (updateInfo.status) {
      case 'downloading':
        return <span className="loading loading-spinner loading-md text-primary w-8 h-8"></span>;
      case 'downloaded':
      case 'ready':
        return <Check className="text-success" size={20} />;
      case 'error':
        return <TriangleAlert className="text-error" size={20} />;
      default:
        return <span className="loading loading-spinner loading-md text-primary"></span>;
    }
  };

  const handleInstallClick = () => {
    sendMessageToBackend('ApplyUpdate');
    clearUpdateInfo();
  };

  // Compact version for the sidebar
  return (
    <div className="w-full px-2 py-1">
      <div className="bg-base-300 border border-base-400 border-opacity-75 shadow-lg rounded-lg p-2 relative">
        {/* Header with status and version */}
        <div className="flex items-center justify-between mb-2 p-1">
          <div className="flex items-center gap-2">
            <div className="flex items-center justify-center w-8 h-8 rounded-full">
              {getStatusIcon()}
            </div>
            <div>
              <h3 className="text-sm font-bold text-white">
                {updateInfo.status === 'downloading'
                  ? t('update.status.inProgress')
                  : t('update.status.available')}
              </h3>
              <p className="text-xs text-gray-400">
                {t('update.version', { version: updateInfo.version })}
              </p>
            </div>
          </div>
        </div>
        {updateInfo.status !== 'downloading' && (
          <button
            onClick={clearUpdateInfo}
            className="absolute top-2.5 right-1 p-1 rounded hover:bg-base-100 transition-colors cursor-pointer"
            aria-label={t('update.dismiss')}
          >
            <X size={14} />
          </button>
        )}

        {/* Action Buttons */}
        <div className="flex flex-col gap-2">
          <Button
            variant="primary"
            size="sm"
            className="no-animation"
            disabled={updateInfo.progress !== 100}
            onClick={handleInstallClick}
          >
            <Download size={14} />
            {t('update.installNow')}
          </Button>
          <Button
            variant="primary"
            size="sm"
            className="no-animation"
            onClick={() => openReleaseNotesModal(__APP_VERSION__)}
          >
            <GithubIcon size={14} />
            {t('update.releaseNotes')}
          </Button>
        </div>
      </div>
    </div>
  );
}
