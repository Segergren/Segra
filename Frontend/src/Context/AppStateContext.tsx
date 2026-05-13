import { createContext, useContext, useState, ReactNode, useEffect } from 'react';
import { State, initialState, GameListEntry } from '../Models/types';

const AppStateContext = createContext<State>(initialState);

export function useAppState(): State {
  return useContext(AppStateContext);
}

interface AppStateProviderProps {
  children: ReactNode;
}

export function AppStateProvider({ children }: AppStateProviderProps) {
  const STORAGE_KEY = 'segra.appstate.v1';

  const loadCachedState = (): State => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return initialState;
      const cached = JSON.parse(raw);
      const revived: State = { ...initialState, ...cached };
      // Do not restore live recording info from cache
      revived.recording = undefined;
      revived.preRecording = undefined;
      revived.hasLoadedObs = false;
      return revived;
    } catch {
      return initialState;
    }
  };

  const saveCachedState = (value: State) => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
    } catch {
      // ignore caching errors
    }
  };

  const [appState, setAppState] = useState<State>(() => loadCachedState());

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;

      if (data.method === 'State') {
        setAppState((prev) => {
          const next: State = { ...prev, ...data.content };
          saveCachedState(next);
          return next;
        });
      } else if (data.method === 'GameList') {
        setAppState((prev) => {
          const next: State = { ...prev, gameList: data.content as GameListEntry[] };
          saveCachedState(next);
          return next;
        });
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, []);

  return <AppStateContext.Provider value={appState}>{children}</AppStateContext.Provider>;
}
