import {
  createContext,
  useContext,
  ReactNode,
  useState,
  useEffect,
  useMemo,
  useCallback,
} from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';

export interface ContentMigrationProgress {
  id: string;
  fileName: string;
  progress: number;
  status: 'migrating' | 'done' | 'error' | 'cancelled';
  totalFiles: number;
  currentFileIndex: number;
  pendingContentIds: string[];
  message?: string;
}

interface ContentMigrationContextType {
  migrations: Record<string, ContentMigrationProgress>;
  isMigrating: boolean;
  // Content still to be moved: 'moving' for the file in progress, 'waiting' for the rest
  getContentMigrationStatus: (contentId: string) => ContentMigrationStatus | null;
  cancelMigration: () => void;
}

export type ContentMigrationStatus = 'moving' | 'waiting';

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

        if (migration.status !== 'migrating') {
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
  // The backend lists pending content in move order, so the first id is the one being moved
  const contentStatuses = useMemo(() => {
    const statuses = new Map<string, ContentMigrationStatus>();
    for (const m of Object.values(migrations)) {
      if (m.status !== 'migrating') continue;
      (m.pendingContentIds ?? []).forEach((id, i) =>
        statuses.set(id, i === 0 ? 'moving' : 'waiting'),
      );
    }
    return statuses;
  }, [migrations]);
  const getContentMigrationStatus = useCallback(
    (contentId: string) => contentStatuses.get(contentId) ?? null,
    [contentStatuses],
  );

  const cancelMigration = useCallback(() => sendMessageToBackend('CancelContentMigration'), []);

  return (
    <ContentMigrationContext.Provider
      value={{ migrations, isMigrating, getContentMigrationStatus, cancelMigration }}
    >
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
