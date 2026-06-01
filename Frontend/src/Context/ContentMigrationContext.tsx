import { createContext, useContext, ReactNode, useState, useEffect } from 'react';

export interface ContentMigrationProgress {
  id: string;
  fileName: string;
  progress: number;
  status: 'migrating' | 'done' | 'error';
  totalFiles: number;
  currentFileIndex: number;
  message?: string;
}

interface ContentMigrationContextType {
  migrations: Record<string, ContentMigrationProgress>;
  isMigrating: boolean;
}

const ContentMigrationContext = createContext<ContentMigrationContextType | undefined>(undefined);

export function ContentMigrationProvider({ children }: { children: ReactNode }) {
  const [migrations, setMigrations] = useState<Record<string, ContentMigrationProgress>>({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'ContentMigrationProgress') {
        const migration = data.content as ContentMigrationProgress;
        setMigrations((prev) => ({
          ...prev,
          [migration.id]: migration,
        }));

        if (migration.status === 'done' || migration.status === 'error') {
          setTimeout(() => {
            setMigrations((prev) => {
              const next = { ...prev };
              delete next[migration.id];
              return next;
            });
          }, 4000);
        }
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const isMigrating = Object.values(migrations).some((m) => m.status === 'migrating');

  return (
    <ContentMigrationContext.Provider value={{ migrations, isMigrating }}>
      {children}
    </ContentMigrationContext.Provider>
  );
}

export function useContentMigration() {
  const context = useContext(ContentMigrationContext);
  if (!context) {
    throw new Error('useContentMigration must be used within a ContentMigrationProvider');
  }
  return context;
}
