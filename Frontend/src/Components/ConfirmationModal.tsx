import { MdWarning } from 'react-icons/md';

export interface ConfirmationModalProps {
  title: string;
  description: string;
  confirmText?: string;
  cancelText?: string;
  onConfirm: () => void;
  onCancel: () => void;
}

export default function ConfirmationModal({
  title,
  description,
  confirmText = 'Confirm',
  cancelText = 'Cancel',
  onConfirm,
  onCancel,
}: ConfirmationModalProps) {
  return (
    <>
      {/* Header */}
      <div className="modal-header pb-4 border-b border-gray-700">
        <div className="flex items-center">
          <span className="text-3xl mr-3 flex items-center">
            <MdWarning className="text-warning" size={32} />
          </span>
          <h2 className="font-bold text-3xl mb-0 text-warning">{title}</h2>
        </div>
        <button
          className="btn btn-circle btn-ghost absolute right-4 top-4 z-10 text-2xl hover:bg-white/10"
          onClick={onCancel}
        >
          ✕
        </button>
      </div>

      <div className="modal-body py-2 mt-4">
        <div className="text-gray-300 text-lg whitespace-pre-line">{description}</div>
      </div>

      {/* Footer with buttons */}
      <div className="modal-action mt-6 flex justify-end gap-3">
        <button className="btn btn-secondary bg-base-300 border-base-400 hover:border-base-400 hover:bg-base-200" onClick={onCancel}>
          {cancelText}
        </button>
        <button className="btn btn-ghost bg-error/20 hover:bg-error/10 text-error border-error" onClick={onConfirm}>
          {confirmText}
        </button>
      </div>
    </>
  );
}
