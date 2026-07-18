import { createContext, useContext, ReactNode, useState, useEffect } from 'react';

interface OcrDownloadContextType {
  ocrDownloadProgress: number | null;
  ocrDownloadStatus: string | null;
}

const OcrDownloadContext = createContext<OcrDownloadContextType | undefined>(undefined);

export function OcrDownloadProvider({ children }: { children: ReactNode }) {
  const [ocrDownloadProgress, setOcrDownloadProgress] = useState<number | null>(null);
  const [ocrDownloadStatus, setOcrDownloadStatus] = useState<string | null>(null);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'OcrDownloadProgress') {
        setOcrDownloadProgress(data.content.progress);
        setOcrDownloadStatus(data.content.status);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  return (
    <OcrDownloadContext.Provider value={{ ocrDownloadProgress, ocrDownloadStatus }}>
      {children}
    </OcrDownloadContext.Provider>
  );
}

export function useOcrDownload() {
  const context = useContext(OcrDownloadContext);
  if (!context) {
    throw new Error('useOcrDownload must be used within an OcrDownloadProvider');
  }
  return context;
}
