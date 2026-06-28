import { createContext, useContext, ReactNode, useState, useEffect } from 'react';
import { AiProgress } from '../Models/types';

interface AiLowlightsContextType {
  aiProgress: Record<string, AiProgress>;
  removeAiLowlight: (id: string) => void;
}

const AiLowlightsContext = createContext<AiLowlightsContextType | undefined>(undefined);

export function AiLowlightsProvider({ children }: { children: ReactNode }) {
  const [aiProgress, setAiProgress] = useState<Record<string, AiProgress>>({});

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<{ method: string; content: any }>) => {
      const { method, content } = event.detail;

      if (method === 'LowlightAiProgress') {
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

  const removeAiLowlight = (id: string) => {
    setAiProgress((prev) => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  return (
    <AiLowlightsContext.Provider value={{ aiProgress, removeAiLowlight }}>
      {children}
    </AiLowlightsContext.Provider>
  );
}

export function useAiLowlights() {
  const context = useContext(AiLowlightsContext);
  if (!context) {
    throw new Error('useAiLowlights must be used within an AiLowlightsProvider');
  }
  return context;
}
