import React, { createContext, useContext, useState, useCallback, useMemo } from 'react';

type ScrollPositions = {
  sessions: number;
  clips: number;
  highlights: number;
  replayBuffer: number;
};

interface ScrollContextType {
  scrollPositions: ScrollPositions;
  setScrollPosition: (page: keyof ScrollPositions, position: number) => void;
}

const ScrollContext = createContext<ScrollContextType | undefined>(undefined);

export function ScrollProvider({ children }: { children: React.ReactNode }) {
  const [scrollPositions, setScrollPositions] = useState<ScrollPositions>({
    sessions: 0,
    clips: 0,
    highlights: 0,
    replayBuffer: 0,
  });

  const setScrollPosition = useCallback((page: keyof ScrollPositions, position: number) => {
    setScrollPositions((prev) => ({
      ...prev,
      [page]: position,
    }));
  }, []);

  const contextValue = useMemo(
    () => ({ scrollPositions, setScrollPosition }),
    [scrollPositions, setScrollPosition],
  );

  return <ScrollContext.Provider value={contextValue}>{children}</ScrollContext.Provider>;
}

export function useScroll() {
  const context = useContext(ScrollContext);
  if (context === undefined) {
    throw new Error('useScroll must be used within a ScrollProvider');
  }
  return context;
}
