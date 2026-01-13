import { createContext, useContext, ReactNode, useState, useEffect } from 'react';

interface ObsDownloadContextType {
  obsDownloadProgress: number | null;
}

const ObsDownloadContext = createContext<ObsDownloadContextType | undefined>(undefined);

export function ObsDownloadProvider({ children }: { children: ReactNode }) {
  const [obsDownloadProgress, setObsDownloadProgress] = useState<number | null>(null);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'ObsDownloadProgress') {
        setObsDownloadProgress(data.content.progress);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  return (
    <ObsDownloadContext.Provider value={{ obsDownloadProgress }}>
      {children}
    </ObsDownloadContext.Provider>
  );
}

export function useObsDownload() {
  const context = useContext(ObsDownloadContext);
  if (!context) {
    throw new Error('useObsDownload must be used within an ObsDownloadProvider');
  }
  return context;
}
