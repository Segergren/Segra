import {createContext, useContext, useState, ReactNode, useEffect} from 'react';
import {Settings, initialSettings, initialState} from '../Models/types';
import {useWebSocketContext} from './WebSocketContext';
import { sendMessageToBackend } from '../Utils/MessageUtils';

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

export function SettingsProvider({children}: SettingsProviderProps) {
	const STORAGE_KEY = 'segra.settings.v1';

	const loadCachedSettings = (): Settings | null => {
		try {
			const raw = localStorage.getItem(STORAGE_KEY);
			if (!raw) return null;
			const cached = JSON.parse(raw);

			// Merge cached settings with defaults
			const revived: Settings = {
				...initialSettings,
				...cached,
				state: {
					...initialState,
					...cached.state,
				},
			};

			// Do not restore ongoing recording/preRecording or hasLoadedObs from cache
			revived.state.recording = undefined;
			revived.state.preRecording = undefined;
			revived.state.hasLoadedObs = false;

			return revived;
		} catch {
			return null;
		}
	};

	const saveCachedSettings = (value: Settings) => {
		try {
			localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
		} catch {
			// ignore caching errors
		}
	};

	const [settings, setSettings] = useState<Settings>(() => loadCachedSettings() ?? initialSettings);
	useWebSocketContext();

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
	}, []);

	const updateSettings: SettingsUpdateContextType = (newSettings, fromBackend = false) => {
		const updatedSettings = {
			...settings,
			...newSettings,
			state: {
				...settings.state,
				...newSettings.state,
			},
		};

		setSettings(updatedSettings);
		// Persist stable settings for faster startup rendering before backend connects
		saveCachedSettings(updatedSettings);

		// Only send UpdateSettings to backend if the change is from the frontend
		if (!fromBackend) {
			sendMessageToBackend('UpdateSettings', updatedSettings);
		}
	};

	return (
		<SettingsContext.Provider value={settings}>
			<SettingsUpdateContext.Provider value={updateSettings}>
				{children}
			</SettingsUpdateContext.Provider>
		</SettingsContext.Provider>
	);
}
