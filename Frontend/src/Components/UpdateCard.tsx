import { useUpdate } from '../Context/UpdateContext';
import { FaDownload, FaCheck, FaExclamationTriangle } from 'react-icons/fa';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { SiGithub } from 'react-icons/si';
import Button from './Button';

export default function UpdateCard() {
  const { updateInfo, openReleaseNotesModal, clearUpdateInfo } = useUpdate();

  if (!updateInfo) return null;

  const getStatusIcon = () => {
    switch (updateInfo.status) {
      case 'downloading':
        return <span className="loading loading-spinner loading-md text-primary w-8 h-8"></span>;
      case 'downloaded':
      case 'ready':
        return <FaCheck className="text-success text-xl" />;
      case 'error':
        return <FaExclamationTriangle className="text-error text-xl" />;
      default:
        return <span className="loading loading-spinner loading-md text-primary"></span>;
    }
  };

  const handleInstallClick = () => {
    // Send a message to the backend to restart the application and install the update
    sendMessageToBackend('ApplyUpdate');
    clearUpdateInfo();
  };

  // Compact version for the sidebar
  return (
    <div className="w-full px-2 py-1">
      <div className="bg-base-300 border border-base-400 border-opacity-75 shadow-lg rounded-lg p-2">
        {/* Header with status and version */}
        <div className="flex items-center justify-between mb-2 p-1">
          <div className="flex items-center gap-2">
            <div className="flex items-center justify-center w-8 h-8 rounded-full">
              {getStatusIcon()}
            </div>
            <div>
              <h3 className="text-sm font-bold text-white">
                {updateInfo.status === 'downloading' ? 'Update in Progress' : 'Update Available'}
              </h3>
              <p className="text-xs text-gray-400">Version {updateInfo.version}</p>
            </div>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="flex flex-col gap-2">
          <Button
            variant="primary"
            size="sm"
            className="no-animation"
            disabled={updateInfo.progress !== 100}
            onClick={handleInstallClick}
          >
            <FaDownload />
            Install Now
          </Button>
          <Button
            variant="primary"
            size="sm"
            className="no-animation"
            onClick={() => openReleaseNotesModal(__APP_VERSION__)}
          >
            <SiGithub />
            Release Notes
          </Button>
        </div>
      </div>
    </div>
  );
}
