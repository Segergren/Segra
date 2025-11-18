import { MdInfo, MdWarning, MdError } from 'react-icons/md';

export interface ModalProps {
  title: string;
  subtitle?: string;
  description: string;
  type: 'info' | 'warning' | 'error';
  onClose: () => void;
}

export default function GenericModal({ title, subtitle, description, type, onClose }: ModalProps) {
  // Define icon and colors based on type
  const getTypeStyles = () => {
    switch (type) {
      case 'info':
        return {
          icon: <MdInfo className="text-blue-500" size={32} />,
          titleColor: 'text-white',
        };
      case 'warning':
        return {
          icon: <MdWarning className="text-warning" size={32} />,
          titleColor: 'text-warning',
        };
      case 'error':
        return {
          icon: <MdError className="text-error" size={32} />,
          titleColor: 'text-error',
        };
      default:
        return {
          icon: <MdInfo className="text-blue-500" size={32} />,
          titleColor: 'text-white',
        };
    }
  };

  const { icon, titleColor } = getTypeStyles();

  return (
    <>
      {/* Header */}
      <div className="modal-header pb-4 border-b border-gray-700">
        <div className="flex items-center">
          <span className="text-3xl mr-3 flex items-center">{icon}</span>
          <h2 className={`font-bold text-3xl mb-0 ${titleColor}`}>{title}</h2>
        </div>
        {subtitle && <p className="text-gray-400 text-lg mt-2">{subtitle}</p>}
        <button
          className="btn btn-circle btn-ghost absolute right-4 top-4 z-10 text-2xl hover:bg-white/10"
          onClick={onClose}
        >
          ✕
        </button>
      </div>

      <div className={`modal-body py-2 mt-4`}>
        <div className="text-gray-300 text-lg whitespace-pre-line">{description}</div>
      </div>
    </>
  );
}
