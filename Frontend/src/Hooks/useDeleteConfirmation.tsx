import { ReactNode, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import ConfirmationModal from '../Components/ConfirmationModal';
import { useModal } from '../Context/ModalContext';
import { useSettings } from '../Context/SettingsContext';

interface DeleteConfirmationOptions {
  title?: string;
  description: ReactNode;
  confirmText?: string;
  onConfirm: () => void;
}

export function useDeleteConfirmation() {
  const { openModal, closeModal } = useModal();
  const settings = useSettings();
  const { t } = useTranslation('modals');

  return useCallback(
    ({
      title = t('deleteConfirm.title'),
      description,
      confirmText = t('deleteConfirm.confirm'),
      onConfirm,
    }: DeleteConfirmationOptions) => {
      if (!settings.confirmBeforeDeleting) {
        onConfirm();
        return;
      }

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
    [closeModal, openModal, settings.confirmBeforeDeleting, t],
  );
}
