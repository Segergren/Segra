import { createContext, useContext, useState, useEffect, useRef, type ReactNode } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useSelections } from './SelectionsContext';
import { useSettings } from './SettingsContext';
import { Selection } from '../Models/types';

export interface ClippingProgress {
  id: number;
  progress: number;
  selections: Selection[];
  error?: string;
}

export interface ClippingContextType {
  clippingProgress: Record<number, ClippingProgress>;
  removeClipping: (id: number) => void;
  cancelClip: (id: number) => void;
}

export const ClippingContext = createContext<ClippingContextType | undefined>(undefined);

export function ClippingProvider({ children }: { children: ReactNode }) {
  const [clippingProgress, setClippingProgress] = useState<Record<number, ClippingProgress>>({});
  const suppressedIds = useRef<Set<number>>(new Set());
  const { removeSelection } = useSelections();
  const settings = useSettings();

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<{ method: string; content: any }>) => {
      const { method, content } = event.detail;

      if (method === 'ClipProgress') {
        const progress = content as ClippingProgress;

        // Suppress messages for cancelled clips
        if (suppressedIds.current.has(progress.id)) {
          return;
        }

        setClippingProgress((prev) => ({
          ...prev,
          [progress.id]: progress,
        }));

        if (progress.progress === 100) {
          // If setting is enabled, remove all selections that were in the clip
          if (
            settings.clipClearSelectionsAfterCreatingClip &&
            progress.selections &&
            progress.selections.length > 0
          ) {
            // Remove each selection that was included in the clip
            progress.selections.forEach((selection) => {
              removeSelection(selection.id);
            });
          }

          setClippingProgress((prev) => {
            const { [progress.id]: _, ...rest } = prev;
            return rest;
          });
        } else if (progress.progress === -1) {
          // Error occurred - keep in progress list briefly to show error, then remove
          console.error('Clip creation failed:', progress.error);
          setTimeout(() => {
            setClippingProgress((prev) => {
              const { [progress.id]: _, ...rest } = prev;
              return rest;
            });
          }, 5000); // Remove after 5 seconds so user can see the error
        }
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, [settings.clipClearSelectionsAfterCreatingClip, removeSelection]);

  const removeClipping = (id: number) => {
    setClippingProgress((prev) => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  const cancelClip = (id: number) => {
    suppressedIds.current.add(id);
    sendMessageToBackend('CancelClip', { id });
    setClippingProgress((prev) => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  return (
    <ClippingContext.Provider value={{ clippingProgress, removeClipping, cancelClip }}>
      {children}
    </ClippingContext.Provider>
  );
}

export function useClipping() {
  const context = useContext(ClippingContext);
  if (!context) {
    throw new Error('useClipping must be used within a ClippingProvider');
  }
  return context;
}
