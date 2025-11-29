import { createContext, useContext, useState, ReactNode, useEffect } from 'react';
import {
  isUpdateProgressMessage,
  isReleaseNotesMessage,
  ReleaseNote,
  isShowReleaseNotesMessage,
  isShowModalMessage,
  isStorageWarningMessage,
  ModalMessage,
  StorageWarningMessage,
} from '../Models/WebSocketMessages';
import { useModal } from './ModalContext';
import ReleaseNotesModal from '../Components/ReleaseNotesModal';
import GenericModal from '../Components/GenericModal';
import ConfirmationModal from '../Components/ConfirmationModal';
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
  openReleaseNotesModal: (filterVersion?: string | null) => void;
  openModal: (modalData: ModalMessage) => void;
  clearUpdateInfo: () => void;
  checkForUpdates: () => void;
}

const UpdateContext = createContext<UpdateContextType | undefined>(undefined);

export function UpdateProvider({ children }: { children: ReactNode }) {
  const [updateInfo, setUpdateInfo] = useState<UpdateProgress | null>(null);
  const [releaseNotes, setReleaseNotes] = useState<ReleaseNote[]>([]);
  const { openModal, closeModal } = useModal();

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

      if (isShowModalMessage(message)) {
        openGenericModal(message.content);
      }

      if (isStorageWarningMessage(message)) {
        openStorageWarningModal(message.content);
      }
    };

    // Listen for WebSocket messages
    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    // Listen for the custom show-release-notes event
    const handleShowReleaseNotes = (event: CustomEvent<any>) => {
      if (event.detail && event.detail.filterVersion) {
        openReleaseNotesModal(event.detail.filterVersion);
      }
    };

    window.addEventListener('show-release-notes', handleShowReleaseNotes as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
      window.removeEventListener('show-release-notes', handleShowReleaseNotes as EventListener);
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
    openModal(<ReleaseNotesModal onClose={closeModal} filterVersion={filterVersion} />);
  };

  const openGenericModal = (modalData: ModalMessage) => {
    openModal(
      <GenericModal
        title={modalData.title}
        subtitle={modalData.subtitle}
        description={modalData.description}
        type={modalData.type}
        onClose={closeModal}
      />,
    );
  };

  const openStorageWarningModal = (warningData: StorageWarningMessage) => {
    openModal(
      <ConfirmationModal
        title={warningData.title}
        description={warningData.description}
        confirmText={warningData.confirmText}
        cancelText={warningData.cancelText}
        onConfirm={() => {
          // Send confirmation to backend
          sendMessageToBackend('StorageWarningConfirm', {
            warningId: warningData.warningId,
            confirmed: true,
            action: warningData.action,
            actionData: warningData.actionData,
          });
          closeModal();
        }}
        onCancel={() => {
          // Send cancellation to backend
          sendMessageToBackend('StorageWarningConfirm', {
            warningId: warningData.warningId,
            confirmed: false,
            action: warningData.action,
            actionData: warningData.actionData,
          });
          closeModal();
        }}
      />,
    );
  };

  return (
    <UpdateContext.Provider
      value={{
        updateInfo,
        releaseNotes,
        openReleaseNotesModal,
        openModal: openGenericModal,
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
