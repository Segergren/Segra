import { createContext, useContext, useState, ReactNode, useEffect, useRef } from 'react';
import {
  isUpdateProgressMessage,
  isReleaseNotesMessage,
  ReleaseNote,
  isShowReleaseNotesMessage,
} from '../Models/WebSocketMessages';
import { useModal } from './ModalContext';
import ReleaseNotesModal from '../Components/ReleaseNotesModal';
import { ReleaseNotesContext } from '../App';
import { sendMessageToBackend } from '../Utils/MessageUtils';

export interface UpdateProgress {
  version: string;
  progress: number;
  status: 'downloading' | 'downloaded' | 'ready' | 'error';
  message: string;
}

interface UpdateContextType {
  updateInfo: UpdateProgress | null;
  releaseNotes: ReleaseNote[];
  // False on Linux (Flatpak); defaults to true until the backend's AppVersion message arrives.
  canSelfUpdate: boolean;
  openReleaseNotesModal: (filterVersion?: string | null) => void;
  clearUpdateInfo: () => void;
  checkForUpdates: () => void;
}

const UpdateContext = createContext<UpdateContextType | undefined>(undefined);

export function UpdateProvider({ children }: { children: ReactNode }) {
  const [updateInfo, setUpdateInfo] = useState<UpdateProgress | null>(null);
  const [releaseNotes, setReleaseNotes] = useState<ReleaseNote[]>([]);
  const [canSelfUpdate, setCanSelfUpdate] = useState<boolean>(true);
  const { openModal, closeModal } = useModal();
  const versionCheckHandled = useRef(false);

  // Mocked update info for testing purposes
  // Uncomment the following useEffect to use mocked data
  /*
  // Downloading
  useEffect(() => {
    setUpdateInfo({
      version: '1.2.3',
      progress: 75,
      status: 'downloading',
      message: 'Downloading update...',
    });
  }, []);

  // Ready to install
  useEffect(() => {
    setUpdateInfo({
      version: '1.2.3',
      progress: 100,
      status: 'ready',
      message: 'Update ready to install',
    });
  }, []);
  */

  // Access the global release notes context
  const globalReleaseNotes = useContext(ReleaseNotesContext);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const message = event.detail;

      if (isUpdateProgressMessage(message)) {
        setUpdateInfo(message.content);
      }

      if (message.method === 'AppVersion' && message.content) {
        if (typeof message.content.canSelfUpdate === 'boolean') {
          setCanSelfUpdate(message.content.canSelfUpdate);
        }

        // Open "What's New" when the version changed since the last run. Compared against a
        // persisted record, not __APP_VERSION__: unstamped builds never match the backend.
        const backendVersion = message.content.version;
        if (backendVersion && !versionCheckHandled.current) {
          versionCheckHandled.current = true;
          const previous = localStorage.getItem('loadedAppVersion');
          if (previous !== backendVersion) {
            localStorage.setItem('loadedAppVersion', backendVersion);
            if (previous && /^\d+\.\d+/.test(previous)) {
              openReleaseNotesModal(previous);
            }
          }
        }
      }

      if (isReleaseNotesMessage(message)) {
        // Handle the ReleaseNotes message
        if (message.content && message.content.releaseNotesList) {
          setReleaseNotes(message.content.releaseNotesList);
          // Also update the global release notes
          globalReleaseNotes.setReleaseNotes(message.content.releaseNotesList);
        }
      }

      if (isShowReleaseNotesMessage(message)) {
        openReleaseNotesModal(message.content);
      }
    };

    // Listen for WebSocket messages
    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    // Leftover from the removed reload-based version check; clear it so it can't linger forever.
    localStorage.removeItem('oldAppVersion');

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const clearUpdateInfo = () => {
    setUpdateInfo(null);
    setReleaseNotes([]);
  };

  const checkForUpdates = () => {
    sendMessageToBackend('CheckForUpdates');
  };

  const openReleaseNotesModal = (filterVersion: string | null = __APP_VERSION__) => {
    openModal(<ReleaseNotesModal onClose={closeModal} filterVersion={filterVersion} />, {
      size: 'xl',
    });
  };

  return (
    <UpdateContext.Provider
      value={{
        updateInfo,
        releaseNotes,
        canSelfUpdate,
        openReleaseNotesModal,
        clearUpdateInfo,
        checkForUpdates,
      }}
    >
      {children}
    </UpdateContext.Provider>
  );
}

export function useUpdate() {
  const context = useContext(UpdateContext);
  if (context === undefined) {
    throw new Error('useUpdate must be used within an UpdateProvider');
  }
  return context;
}
