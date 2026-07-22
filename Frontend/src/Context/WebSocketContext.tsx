import { createContext, useContext, ReactNode, useCallback, useEffect, useRef } from 'react';
import useWebSocket, { ReadyState } from 'react-use-websocket';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useAuth } from '../Hooks/useAuth.tsx';

interface WebSocketContextType {
  sendMessage: (message: string) => void;
  isConnected: boolean;
  connectionState: ReadyState;
}

const WebSocketContext = createContext<WebSocketContextType | undefined>(undefined);

interface WebSocketMessage {
  method: string;
  content: any;
}

export function WebSocketProvider({ children }: { children: ReactNode }) {
  // Get the auth session to properly handle authentication
  const { session } = useAuth();
  // Ref to track if we've already handled a version mismatch (prevent multiple reloads)
  const versionCheckHandled = useRef(false);
  // Ref to track if this is a reconnection (not initial connection)
  const hasConnectedBefore = useRef(false);

  // Log when the WebSocket provider mounts or session changes
  useEffect(() => {
    console.log('WebSocketProvider: Session state changed:', !!session);
  }, [session]);

  // Configure WebSocket with reconnection and heartbeat
  const { readyState } = useWebSocket('ws://localhost:44030/', {
    onOpen: () => {
      // Check if this is a reconnection
      if (hasConnectedBefore.current) {
        console.log('WebSocket reconnected after disconnect - resyncing state');
      } else {
        console.log('WebSocket connected for the first time');
        hasConnectedBefore.current = true;
      }

      sendMessageToBackend('NewConnection');

      // If we already have a session when connecting, ensure we're logged in
      if (session) {
        console.log('WebSocket connected with active session, ensuring login state');
        sendMessageToBackend('Login', {
          accessToken: session.access_token,
          refreshToken: session.refresh_token,
        });
      }
    },
    onClose: (event) => {
      console.warn('WebSocket closed:', event.code, event.reason);
    },
    onError: (event) => {
      console.error('WebSocket error:', event);
    },
    onMessage: (event) => {
      try {
        const data: WebSocketMessage = JSON.parse(event.data);
        if (data.method !== 'RecordingPreviewFrame') {
          console.log('WebSocket message received:', data);
        }

        // Handle version check
        if (data.method === 'AppVersion' && !versionCheckHandled.current) {
          versionCheckHandled.current = true;
          const backendVersion = data.content?.version;

          // Reloading exists to pick up a freshly-updated frontend when the app updates itself while
          // running. We decide from a PERSISTED record of the version we actually loaded - not from
          // the build-time __APP_VERSION__ constant. On Linux the frontend is served from the
          // packaged wwwroot and its build constant may never equal the backend's packaged version
          // (e.g. an unstamped "Developer Preview" build), which made the old constant-comparison
          // reload on every single launch. Comparing against a stored value is self-correcting:
          // after one reload it matches and stops. If localStorage is unavailable it simply never
          // reloads, which is harmless (the packaged frontend is already current).
          if (backendVersion) {
            const loaded = localStorage.getItem('loadedAppVersion');
            if (loaded == null) {
              // First launch on this machine (or cleared storage): adopt, never reload.
              localStorage.setItem('loadedAppVersion', backendVersion);
            } else if (loaded !== backendVersion) {
              // The backend was updated under a running frontend: reload once for the new UI.
              console.log(`App version changed: ${loaded} -> ${backendVersion}. Reloading once...`);
              localStorage.setItem('loadedAppVersion', backendVersion);
              localStorage.setItem('oldAppVersion', loaded);
              window.location.reload();
              return;
            }
          }
        }

        // Dispatch the message to all listeners
        window.dispatchEvent(
          new CustomEvent('websocket-message', {
            detail: data,
          }),
        );
      } catch (error) {
        console.error('Failed to parse WebSocket message:', error);
      }
    },
    shouldReconnect: () => {
      console.log('WebSocket closed, will attempt to reconnect');
      return true;
    },
    reconnectAttempts: Infinity,
    reconnectInterval: 3000,
    // The heartbeat closes the socket if no message arrives within `timeout`, and otherwise
    // sends `message` every `interval`. Both run off a single setInterval. While the Segra
    // window is backgrounded during gameplay, Chromium/WebView2 throttles timers to fire at
    // most about once every 60 seconds. `interval` must stay below that floor so each throttled
    // tick still emits a ping (which the backend answers, resetting the timeout), and `timeout`
    // must stay well above it so one slow tick can't trip the close.
    heartbeat: {
      message: 'ping',
      timeout: 120000,
      interval: 30000,
    },
  });

  const contextValue = {
    sendMessage: useCallback((message: string) => {
      sendMessageToBackend(message);
    }, []),
    isConnected: readyState === ReadyState.OPEN,
    connectionState: readyState,
  };

  return <WebSocketContext.Provider value={contextValue}>{children}</WebSocketContext.Provider>;
}

export function useWebSocketContext() {
  const context = useContext(WebSocketContext);
  if (!context) {
    throw new Error('useWebSocketContext must be used within a WebSocketProvider');
  }
  return context;
}
