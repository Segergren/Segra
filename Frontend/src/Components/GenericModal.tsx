import { useState } from 'react';
import { Info, TriangleAlert, CircleAlert, Copy, Check } from 'lucide-react';
import Button from './Button';

export interface ModalProps {
  title: string;
  subtitle?: string;
  description: string;
  type: 'info' | 'warning' | 'error';
  onClose: () => void;
}

function CodeSnippet({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);

  return (
    <span className="inline-flex items-center gap-2 max-w-full bg-base-100 border border-base-content/10 rounded-md pl-2.5 pr-1 py-1 my-1">
      <code className="font-mono text-sm text-base-content break-all">{text}</code>
      <Button
        variant="ghost"
        size="xs"
        icon
        className="shrink-0"
        onClick={() => {
          navigator.clipboard.writeText(text);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        }}
      >
        {copied ? <Check size={14} /> : <Copy size={14} />}
      </Button>
    </span>
  );
}

export default function GenericModal({ title, subtitle, description, type, onClose }: ModalProps) {
  // Define icon and colors based on type
  const getTypeStyles = () => {
    switch (type) {
      case 'info':
        return {
          icon: <Info className="text-blue-500" size={32} />,
          titleColor: 'text-white',
        };
      case 'warning':
        return {
          icon: <TriangleAlert className="text-warning" size={32} />,
          titleColor: 'text-warning',
        };
      case 'error':
        return {
          icon: <CircleAlert className="text-error" size={32} />,
          titleColor: 'text-error',
        };
      default:
        return {
          icon: <Info className="text-blue-500" size={32} />,
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
        <Button variant="ghost" icon className="absolute right-4 top-4 z-10" onClick={onClose}>
          ✕
        </Button>
      </div>

      <div className={`modal-body py-2 mt-4`}>
        <div className="text-gray-300 text-lg whitespace-pre-line">
          {description
            .split('`')
            .map((part, i) => (i % 2 === 1 ? <CodeSnippet key={i} text={part} /> : part))}
        </div>
      </div>
    </>
  );
}
