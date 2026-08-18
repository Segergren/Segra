import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
// Bundle Roboto so the app renders the same font on every platform (WebView2 on Windows,
// WebKitGTK on Linux) instead of falling back to whatever sans the OS happens to have.
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import './globals.css';
import App from './App.tsx';
import { SelectedVideoProvider } from './Context/SelectedVideoContext.tsx';
import { SelectedMenuProvider } from './Context/SelectedMenuContext';
import { AuthProvider, onSignOut } from './Hooks/useAuth.tsx';

// Create a React Query client
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 1000 * 60 * 5, // 5 minutes
      gcTime: 1000 * 60 * 30, // 30 minutes
    },
  },
});

// Clear query cache on sign out
onSignOut(() => queryClient.clear());

// Segra provides its own context menus where right-click actions are supported.
document.addEventListener('contextmenu', (event) => {
  event.preventDefault();
  window.dispatchEvent(new Event('segra:close-content-context-menus'));
});

// Wait for Roboto to load before the first render, so the UI never appears with a
// fallback font that swaps (and shifts the layout) when the font arrives. The window
// stays visible the whole time; it just shows the app background until the UI is ready.
// The timeout guards against a stalled font load leaving a blank window.
const fontsReady = Promise.race([
  Promise.all([
    document.fonts.load('400 1em Roboto'),
    document.fonts.load('500 1em Roboto'),
    document.fonts.load('700 1em Roboto'),
    document.fonts.ready,
  ]),
  new Promise((resolve) => setTimeout(resolve, 2000)),
]);

const renderApp = () =>
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <SelectedVideoProvider>
            <SelectedMenuProvider>
              <App />
            </SelectedMenuProvider>
          </SelectedVideoProvider>
        </AuthProvider>
      </QueryClientProvider>
    </StrictMode>,
  );

fontsReady.then(renderApp);
