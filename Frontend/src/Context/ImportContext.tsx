import { createContext, useContext, ReactNode, useState, useEffect } from 'react';

export interface ImportProgress {
  id: string;
  fileName: string;
  progress: number;
  status: 'importing' | 'done' | 'error';
  totalFiles: number;
  currentFileIndex: number;
  message?: string;
}

interface ImportContextType {
  imports: Record<string, ImportProgress>;
}

const ImportContext = createContext<ImportContextType | undefined>(undefined);

export function ImportProvider({ children }: { children: ReactNode }) {
  const [imports, setImports] = useState<Record<string, ImportProgress>>({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'ImportProgress') {
        const { id, fileName, progress, status, totalFiles, currentFileIndex, message } =
          data.content;
        setImports((prev) => ({
          ...prev,
          [id]: {
            id,
            fileName,
            progress,
            status,
            totalFiles,
            currentFileIndex,
            message,
          },
        }));

        if (status === 'done' || status === 'error') {
          setTimeout(() => {
            setImports((prev) => {
              const newImports = { ...prev };
              delete newImports[id];
              return newImports;
            });
          }, 3000); // Remove after 3 seconds
        }
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  return <ImportContext.Provider value={{ imports }}>{children}</ImportContext.Provider>;
}

export function useImports() {
  const context = useContext(ImportContext);
  if (!context) {
    throw new Error('useImports must be used within an ImportProvider');
  }
  return context;
}
