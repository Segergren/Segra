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

          // Only reload when the running frontend is a real, versioned build. Reloading is meant to
          // pick up a freshly-updated frontend after an app update; a dev/preview build (e.g.
          // "Developer Preview") can never match a packaged backend version, so reloading would loop
          // forever. Requiring a semver-looking frontend version avoids that without relying on
          // localStorage/URL persistence (which the embedded WebKitGTK webview does not keep reliably).
          const frontendIsVersioned = /^\d+\.\d+/.test(__APP_VERSION__);
          if (backendVersion && backendVersion !== __APP_VERSION__ && frontendIsVersioned) {
            // Belt-and-suspenders: also guard via a URL param so even two mismatched real versions
            // can only trigger a single reload.
            const params = new URLSearchParams(window.location.search);
            if (params.get('reloadedForVersion') === backendVersion) {
              console.warn(
                `Version mismatch persists after reload (backend ${backendVersion}, frontend ${__APP_VERSION__}); not reloading again.`,
              );
            } else {
              console.log(
                `Version mismatch: Backend ${backendVersion}, Frontend ${__APP_VERSION__}. Reloading...`,
              );
              localStorage.setItem('oldAppVersion', __APP_VERSION__);
              params.set('reloadedForVersion', backendVersion);
              window.location.search = params.toString();
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
