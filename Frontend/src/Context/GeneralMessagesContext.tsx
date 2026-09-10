import { createContext, ReactNode, useEffect, useRef } from 'react';
import {
  isShowModalMessage,
  isStorageWarningMessage,
  isRecoveryPromptMessage,
  ModalMessage,
  StorageWarningMessage,
  RecoveryPromptMessage,
} from '../Models/WebSocketMessages';
import { useModal } from './ModalContext';
import GenericModal from '../Components/GenericModal';
import ConfirmationModal from '../Components/ConfirmationModal';
import RecoveryModal from '../Components/RecoveryModal';
import { sendMessageToBackend } from '../Utils/MessageUtils';

const GeneralMessagesContext = createContext<undefined>(undefined);

export function GeneralMessagesProvider({ children }: { children: ReactNode }) {
  const { openModal, closeModal, isModalOpen } = useModal();
  // Backend modals that arrive while another modal is open are shown one at a time.
  const pendingModals = useRef<ModalMessage[]>([]);
  const modalOpenRef = useRef(false);

  useEffect(() => {
    modalOpenRef.current = isModalOpen;
    if (!isModalOpen && pendingModals.current.length > 0) {
      openGenericModal(pendingModals.current.shift()!);
    }
  }, [isModalOpen]);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const message = event.detail;

      if (isShowModalMessage(message)) {
        if (modalOpenRef.current) {
          pendingModals.current.push(message.content);
        } else {
          openGenericModal(message.content);
        }
      }

      if (isStorageWarningMessage(message)) {
        openStorageWarningModal(message.content);
      }

      if (isRecoveryPromptMessage(message)) {
        openRecoveryPromptModal(message.content);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const openGenericModal = (modalData: ModalMessage) => {
    // Set before the re-render so a second message in the same burst queues instead of replacing this one.
    modalOpenRef.current = true;
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
          sendMessageToBackend('StorageWarningConfirm', {
            warningId: warningData.warningId,
            confirmed: true,
            action: warningData.action,
            actionData: warningData.actionData,
          });
          closeModal();
        }}
        onCancel={() => {
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

  const openRecoveryPromptModal = (recoveryData: RecoveryPromptMessage) => {
    openModal(<RecoveryModal files={recoveryData.files} onClose={closeModal} />);
  };

  return (
    <GeneralMessagesContext.Provider value={undefined}>{children}</GeneralMessagesContext.Provider>
  );
}
