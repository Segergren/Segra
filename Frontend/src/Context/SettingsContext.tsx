import {
  createContext,
  useContext,
  useState,
  ReactNode,
  useEffect,
  useCallback,
  useRef,
} from 'react';
import { Settings, initialSettings } from '../Models/types';
import { useWebSocketContext } from './WebSocketContext';
import { sendMessageToBackend } from '../Utils/MessageUtils';

export const SETTINGS_STORAGE_KEY = 'segra.settings.v1';

type SettingsContextType = Settings;
type SettingsUpdateContextType = (newSettings: Partial<Settings>, fromBackend?: boolean) => void;

const SettingsContext = createContext<SettingsContextType>(initialSettings);
const SettingsUpdateContext = createContext<SettingsUpdateContextType>(() => {});

export function useSettings(): SettingsContextType {
  return useContext(SettingsContext);
}

export function useSettingsUpdater(): SettingsUpdateContextType {
  return useContext(SettingsUpdateContext);
}

interface SettingsProviderProps {
  children: ReactNode;
}

export function SettingsProvider({ children }: SettingsProviderProps) {
  const loadCachedSettings = (): Settings | null => {
    try {
      const raw = localStorage.getItem(SETTINGS_STORAGE_KEY);
      if (!raw) return null;
      const cached = JSON.parse(raw);
      return { ...initialSettings, ...cached };
    } catch {
      return null;
    }
  };

  const saveCachedSettings = (value: Settings) => {
    try {
      localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(value));
    } catch {
      // ignore caching errors
    }
  };

  const [settings, setSettings] = useState<Settings>(() => loadCachedSettings() ?? initialSettings);
  useWebSocketContext();

  const pendingBackendUpdateRef = useRef<Settings | null>(null);

  const updateSettings = useCallback<SettingsUpdateContextType>(
    (newSettings, fromBackend = false) => {
      setSettings((prev) => {
        const updatedSettings: Settings = { ...prev, ...newSettings };
        saveCachedSettings(updatedSettings);
        if (!fromBackend) {
          pendingBackendUpdateRef.current = updatedSettings;
        }
        return updatedSettings;
      });
    },
    [],
  );

  useEffect(() => {
    if (pendingBackendUpdateRef.current !== null) {
      const settingsToSend = pendingBackendUpdateRef.current;
      pendingBackendUpdateRef.current = null;
      queueMicrotask(() => {
        sendMessageToBackend('UpdateSettings', settingsToSend);
      });
    }
  }, [settings]);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const data = event.detail;
      if (data.method === 'Settings') {
        updateSettings(data.content, true);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, [updateSettings]);

  return (
    <SettingsContext.Provider value={settings}>
      <SettingsUpdateContext.Provider value={updateSettings}>
        {children}
      </SettingsUpdateContext.Provider>
    </SettingsContext.Provider>
  );
}
