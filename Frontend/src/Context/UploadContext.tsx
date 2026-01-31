import { createContext, useContext, ReactNode, useState, useEffect } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';

export interface UploadProgress {
  title: string;
  fileName: string;
  progress: number;
  status: 'uploading' | 'processing' | 'done' | 'error';
  message?: string;
}

interface UploadContextType {
  uploads: Record<string, UploadProgress>;
  removeUpload: (fileName: string) => void;
  cancelUpload: (fileName: string) => void;
}

const UploadContext = createContext<UploadContextType | undefined>(undefined);

export function UploadProvider({ children }: { children: ReactNode }) {
  const [uploads, setUploads] = useState<Record<string, UploadProgress>>({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'UploadProgress') {
        const { title, fileName, progress, status, message } = data.content;
        setUploads((prev) => ({
          ...prev,
          [fileName]: { title, fileName, progress, status, message },
        }));

        if (status === 'done' || status === 'error') {
          setUploads((prev) => {
            const newUploads = { ...prev };
            delete newUploads[fileName];
            return newUploads;
          });
        }
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const removeUpload = (fileName: string) => {
    setUploads((prev) => {
      const newUploads = { ...prev };
      delete newUploads[fileName];
      return newUploads;
    });
  };

  const cancelUpload = (fileName: string) => {
    sendMessageToBackend('CancelUpload', { fileName });
    setUploads((prev) => {
      const newUploads = { ...prev };
      delete newUploads[fileName];
      return newUploads;
    });
  };

  return (
    <UploadContext.Provider value={{ uploads, removeUpload, cancelUpload }}>
      {children}
    </UploadContext.Provider>
  );
}

export function useUploads() {
  const context = useContext(UploadContext);
  if (!context) {
    throw new Error('useUploads must be used within an UploadProvider');
  }
  return context;
}
