import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
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
