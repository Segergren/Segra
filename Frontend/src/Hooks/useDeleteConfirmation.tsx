import { ReactNode, useCallback } from 'react';
import ConfirmationModal from '../Components/ConfirmationModal';
import { useModal } from '../Context/ModalContext';

interface DeleteConfirmationOptions {
  title?: string;
  description: ReactNode;
  confirmText?: string;
  onConfirm: () => void;
}

export function useDeleteConfirmation() {
  const { openModal, closeModal } = useModal();

  return useCallback(
    ({
      title = 'Delete this item?',
      description,
      confirmText = 'Delete',
      onConfirm,
    }: DeleteConfirmationOptions) => {
      openModal(
        <ConfirmationModal
          title={title}
          description={description}
          confirmText={confirmText}
          onConfirm={() => {
            onConfirm();
            closeModal();
          }}
          onCancel={closeModal}
        />,
        { size: 'md' },
      );
    },
    [closeModal, openModal],
  );
}
