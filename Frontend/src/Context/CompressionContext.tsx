import { createContext, useContext, ReactNode, useState, useEffect } from 'react';

interface CompressionProgress {
  filePath: string;
  progress: number;
  status: 'compressing' | 'done' | 'error' | 'skipped';
  message?: string;
}

interface CompressionContextType {
  compressionProgress: Record<string, CompressionProgress>;
  isCompressing: (filePath: string) => boolean;
}

const CompressionContext = createContext<CompressionContextType | undefined>(undefined);

export function CompressionProvider({ children }: { children: ReactNode }) {
  const [compressionProgress, setCompressionProgress] = useState<
    Record<string, CompressionProgress>
  >({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<{ method: string; content: any }>) => {
      const { method, content } = event.detail;

      if (method === 'CompressionProgress') {
        const progress = content as CompressionProgress;

        if (
          progress.status === 'done' ||
          progress.status === 'error' ||
          progress.status === 'skipped'
        ) {
          setTimeout(() => {
            setCompressionProgress((prev) => {
              const { [progress.filePath]: _, ...rest } = prev;
              return rest;
            });
          }, 2000);
        }

        setCompressionProgress((prev) => ({
          ...prev,
          [progress.filePath]: progress,
        }));
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const isCompressing = (filePath: string) => {
    const progress = compressionProgress[filePath];
    return progress?.status === 'compressing';
  };

  return (
    <CompressionContext.Provider value={{ compressionProgress, isCompressing }}>
      {children}
    </CompressionContext.Provider>
  );
}

export function useCompression() {
  const context = useContext(CompressionContext);
  if (context === undefined) {
    throw new Error('useCompression must be used within a CompressionProvider');
  }
  return context;
}
