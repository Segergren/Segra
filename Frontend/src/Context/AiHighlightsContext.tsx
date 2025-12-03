import { createContext, useContext, ReactNode, useState, useEffect } from 'react';
import { AiProgress } from '../Models/types';

interface AiHighlightsContextType {
  aiProgress: Record<string, AiProgress>;
  removeAiHighlight: (id: string) => void;
}

const AiHighlightsContext = createContext<AiHighlightsContextType | undefined>(undefined);

export function AiHighlightsProvider({ children }: { children: ReactNode }) {
  const [aiProgress, setAiProgress] = useState<Record<string, AiProgress>>({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<{ method: string; content: any }>) => {
      const { method, content } = event.detail;

      if (method === 'AiProgress') {
        const progress = content as AiProgress;
        setAiProgress((prev) => ({
          ...prev,
          [progress.id]: progress,
        }));

        if (progress.status === 'done') {
          setAiProgress((prev) => {
            const { [progress.id]: _, ...rest } = prev;
            return rest;
          });
        } else if (progress.progress < 0) {
          // This is an error, remove error after 5 seconds so user can see the message
          setTimeout(() => {
            setAiProgress((prev) => {
              const { [progress.id]: _, ...rest } = prev;
              return rest;
            });
          }, 5000);
        }
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  const removeAiHighlight = (id: string) => {
    setAiProgress((prev) => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  return (
    <AiHighlightsContext.Provider value={{ aiProgress, removeAiHighlight }}>
      {children}
    </AiHighlightsContext.Provider>
  );
}

export function useAiHighlights() {
  const context = useContext(AiHighlightsContext);
  if (!context) {
    throw new Error('useAiHighlights must be used within an AiHighlightsProvider');
  }
  return context;
}
