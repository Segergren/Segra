import { createContext, useContext, ReactNode, useCallback, useEffect, useRef } from 'react';
import useWebSocket, { ReadyState } from 'react-use-websocket';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useAuth } from '../Hooks/useAuth.tsx';

interface WebSocketContextType {
  sendMessage: (message: string) => void;
  send: (method: string, parameters?: any) => void;
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
  const { readyState, sendJsonMessage } = useWebSocket('ws://localhost:5000/', {
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
        console.log('WebSocket message received:', data);

        // Handle version check
        if (data.method === 'AppVersion' && !versionCheckHandled.current) {
          versionCheckHandled.current = true;
          const backendVersion = data.content?.version;

          if (backendVersion && backendVersion !== __APP_VERSION__) {
            console.log(
              `Version mismatch: Backend ${backendVersion}, Frontend ${__APP_VERSION__}. Reloading...`,
            );
            // Store the old version before reloading
            localStorage.setItem('oldAppVersion', __APP_VERSION__);
            window.location.reload();
            return;
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
    heartbeat: {
      message: 'ping',
      returnMessage: 'pong',
      timeout: 30000,
      interval: 15000,
    },
  });

  const contextValue = {
    sendMessage: useCallback((message: string) => {
      // Try using window.external first (production/webview)
      if ((window as any).external && typeof (window as any).external.sendMessage === 'function') {
        sendMessageToBackend(message);
      } else {
        // Fallback to WebSocket (browser/dev)
        try {
           // We assume message is a JSON string because sendMessageToBackend expects method/params but here we get a string.
           // However, existing usages of context.sendMessage might be passing a string that is just the method name?
           // Looking at usage in components would verify.
           // But assuming it's a JSON string or we should parse it?
           // Actually sendMessageToBackend takes (method, params).
           // This context.sendMessage takes (message: string).
           // If 'message' is a JSON string, we can parse it.
           // If it's just a method name, we wrap it.
           // But let's look at how sendJsonMessage works.
           const parsed = JSON.parse(message);
           sendJsonMessage(parsed);
        } catch (e) {
            // If not JSON, maybe it's just a method name?
            sendJsonMessage({ Method: message });
        }
      }
    }, [sendJsonMessage]),
    send: useCallback((method: string, parameters?: any) => {
        // Preferred way to send structured messages
         if ((window as any).external && typeof (window as any).external.sendMessage === 'function') {
            sendMessageToBackend(method, parameters);
        } else {
            sendJsonMessage({ Method: method, Parameters: parameters });
        }
    }, [sendJsonMessage]),
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
